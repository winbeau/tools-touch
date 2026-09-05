using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Microsoft.Win32;
using ToolsTouch.Core;

namespace ToolsTouch.Desktop;

public sealed record ModelProvider(string Id, string Name, bool Configured, bool OAuth, bool ApiKey, string[] Models);
public sealed record AccountOption(string Id, string Label);

public sealed partial class MainViewModel
{
    private DiagnosticLog diagnostics = null!;
    private Action<Uri> loginBrowser = uri => OpenUrl(uri.AbsoluteUri);
    private Action<string> copyLoginUrl = System.Windows.Clipboard.SetText;
    public bool IsSignedIn => gmailAuth.Account != null;
    public bool NeedsSignIn => !IsSignedIn;
    public string SignedInAccount => gmailAuth.Account ?? "尚未登录";
    private readonly SemaphoreSlim connectionGate = new(1, 1);
    private readonly CancellationTokenSource componentCancellation = new();
    private Task? initialization;
    private bool componentLoading;
    private bool componentReady;
    private bool authBusy;
    private string componentStatus = "组件加载中…";
    private string providerStatus = "请等待组件加载。";
    private string gmailProgress = "点击“使用 Google 登录”，在浏览器中完成授权。";
    private string authPrompt = "";
    private string? authPromptId;
    private string? authUrl;
    private string? lastOpenedAuthUrl;
    private string? gmailAuthUrl;
    public bool AutoOpenLoginBrowser
    {
        get => Settings.AutoOpenLoginBrowser;
        set { if (Settings.AutoOpenLoginBrowser == value) return; Settings.AutoOpenLoginBrowser = value; Settings.Save(); Raise(); }
    }
    public string GmailConnectLabel => IsSignedIn ? "Gmail 已连接" : "使用 Google 登录";
    public ICommand CopyLoginUrlCommand { get; private set; } = null!;
    public ICommand CopyGmailLoginUrlCommand { get; private set; } = null!;
    public ICommand ReauthorizeGmailCommand { get; private set; } = null!;
    private bool authSecret;
    private string providerApiKey = "";
    private ModelProvider? selectedProvider;
    private AccountOption? selectedLoginMethod;
    public ObservableCollection<ModelProvider> Providers { get; } = [];
    public ObservableCollection<AccountOption> LoginMethods { get; } = [];
    public ObservableCollection<AccountOption> AuthChoices { get; } = [];
    public bool HasAuthChoices => AuthChoices.Count > 0;
    public AccountOption? SelectedAuthChoice { get; set; }
    private string authReply = "";
    public string AuthReply { get => authReply; set { Set(ref authReply, value); CommandManager.InvalidateRequerySuggested(); } }
    public string AuthPrompt { get => authPrompt; private set => Set(ref authPrompt, value); }
    public bool AuthSecret { get => authSecret; private set { Set(ref authSecret, value); Raise(nameof(AuthText)); } }
    public bool AuthText => !AuthSecret && authPromptId != null && AuthChoices.Count == 0;
    public string ProviderApiKey { get => providerApiKey; set { Set(ref providerApiKey, value); CommandManager.InvalidateRequerySuggested(); } }
    public string ProviderStatus { get => providerStatus; private set => Set(ref providerStatus, value); }
    public string GmailProgress { get => gmailProgress; private set => Set(ref gmailProgress, value); }
    public bool GoogleClientBundled => File.Exists(Path.Combine(AppContext.BaseDirectory, "config", "google-client.json"));
    public bool ShowGoogleSetup => !GoogleClientBundled;
    public string GmailClientName => string.IsNullOrWhiteSpace(Settings.GoogleClientFile) ? "未配置 Google 桌面 OAuth 客户端" : Path.GetFileName(Settings.GoogleClientFile);
    public string ComponentStatus { get => componentStatus; private set => Set(ref componentStatus, value); }
    public bool ComponentLoading { get => componentLoading; private set { Set(ref componentLoading, value); Raise(nameof(AccountControlsEnabled)); CommandManager.InvalidateRequerySuggested(); } }
    public bool ComponentReady { get => componentReady; private set { Set(ref componentReady, value); Raise(nameof(AccountControlsEnabled)); CommandManager.InvalidateRequerySuggested(); } }
    public bool AuthBusy { get => authBusy; private set { Set(ref authBusy, value); Raise(nameof(AccountControlsEnabled)); CommandManager.InvalidateRequerySuggested(); } }
    public bool AccountControlsEnabled => IsSignedIn && ComponentReady && !ComponentLoading && !AuthBusy && !Busy;
    private bool CanUseModel => AccountControlsEnabled && SelectedProvider?.Configured == true && !string.IsNullOrWhiteSpace(Settings.Model);
    public ModelProvider? SelectedProvider
    {
        get => selectedProvider;
        set
        {
            if (!Set(ref selectedProvider, value)) return;
            LoginMethods.Clear(); Models.Clear();
            if (value == null) return;
            Settings.Provider = value.Id;
            if (value.OAuth)
            {
                LoginMethods.Add(new("browser", "浏览器授权"));
                if (value.Id == "openai-codex") LoginMethods.Add(new("device_code", "设备码授权（回调不可用时）"));
            }
            if (value.ApiKey) LoginMethods.Add(new("api_key", "API Key"));
            SelectedLoginMethod = LoginMethods.FirstOrDefault();
            foreach (var model in value.Models) Models.Add(model);
            if (!Models.Contains(Settings.Model)) Settings.Model = Models.FirstOrDefault() ?? "";
            ProviderStatus = value.Configured ? "已保存凭据；实际调用可用性以服务商响应为准。" : "尚未连接，请选择授权方式。";
            ProviderApiKey = ""; Raise(nameof(Settings)); CommandManager.InvalidateRequerySuggested();
        }
    }
    public AccountOption? SelectedLoginMethod
    {
        get => selectedLoginMethod;
        set { if (Set(ref selectedLoginMethod, value)) { Raise(nameof(ApiKeyRequired)); CommandManager.InvalidateRequerySuggested(); } }
    }
    public bool ApiKeyRequired => SelectedLoginMethod?.Id == "api_key";
    public ICommand CancelAuthCommand { get; private set; } = null!;
    public ICommand ImportGoogleClientCommand { get; private set; } = null!;
    public ICommand CancelGmailCommand { get; private set; } = null!;
    public ICommand OpenLogsCommand { get; private set; } = null!;
    public ICommand ExportLogsCommand { get; private set; } = null!;

    private void ConfigureAccounts()
    {
        UiCommand Command(Func<Task> action, Func<bool>? enabled = null) => new(action, ReportError, enabled);
        ConnectAgentCommand = Command(ConnectAgentAsync, () => !Busy && !AuthBusy && !ComponentLoading);
        LoginCommand = Command(LoginAsync, () => AccountControlsEnabled && SelectedLoginMethod != null && (!ApiKeyRequired || !string.IsNullOrWhiteSpace(ProviderApiKey)));
        OpenLoginCommand = Command(() => { if (authUrl != null) loginBrowser(new Uri(authUrl)); return Task.CompletedTask; }, () => AuthBusy && authUrl != null);
        AuthReplyCommand = Command(async () =>
        {
            var value = AuthChoices.Count > 0 ? SelectedAuthChoice?.Id : AuthReply;
            if (string.IsNullOrWhiteSpace(value)) return;
            await bridge!.RequestAsync(new { type = "auth_reply", id = NewId(), prompt_id = authPromptId, value });
            AuthReply = ""; Raise(nameof(AuthReply));
        }, () => AuthBusy && authPromptId != null && (AuthChoices.Count > 0 ? SelectedAuthChoice != null : !string.IsNullOrWhiteSpace(AuthReply)));
        CancelAuthCommand = Command(async () => { if (bridge != null) await bridge.CancelAsync(); }, () => AuthBusy);
        CopyLoginUrlCommand = Command(() => { copyLoginUrl(authUrl!); ProviderStatus = "登录链接已复制，请在同一台电脑的浏览器中打开。"; return Task.CompletedTask; }, () => AuthBusy && authUrl != null);
        CopyGmailLoginUrlCommand = Command(() => { copyLoginUrl(gmailAuthUrl!); return Task.CompletedTask; }, () => gmailCancellation != null && gmailAuthUrl != null);
        ConnectGmailCommand = Command(() => ConnectGmailAsync(), () => !mailBusy && !IsSignedIn);
        ReauthorizeGmailCommand = Command(() => ConnectGmailAsync(true), () => !mailBusy && IsSignedIn);
        CancelGmailCommand = Command(() => { gmailCancellation?.Cancel(); return Task.CompletedTask; }, () => gmailCancellation != null);
        ImportGoogleClientCommand = Command(() =>
        {
            var dialog = new OpenFileDialog { Filter = "Google OAuth JSON|*.json", Title = "选择 Google 桌面应用客户端配置" };
            if (dialog.ShowDialog() != true) return Task.CompletedTask;
            _ = GoogleClient.FromFile(dialog.FileName);
            var destination = Path.Combine(DataDirectory, "config", "google-client.json");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!string.Equals(Path.GetFullPath(dialog.FileName), destination, StringComparison.OrdinalIgnoreCase)) File.Copy(dialog.FileName, destination, true);
            Settings.GoogleClientFile = destination; Settings.Save(); Raise(nameof(GmailClientName)); Raise(nameof(Settings));
            GmailProgress = "客户端已导入，可以连接 Gmail。";
            diagnostics.Write("gmail", "client_imported");
            return Task.CompletedTask;
        }, () => !mailBusy);
        OpenLogsCommand = Command(() =>
        {
            Directory.CreateDirectory(diagnostics.DirectoryPath);
            Process.Start(new ProcessStartInfo(diagnostics.DirectoryPath) { UseShellExecute = true }); return Task.CompletedTask;
        });
        ExportLogsCommand = Command(() =>
        {
            var dialog = new SaveFileDialog { Filter = "诊断日志 ZIP|*.zip", FileName = $"ToolsTouch-logs-{DateTime.Now:yyyyMMdd-HHmmss}.zip" };
            if (dialog.ShowDialog() != true) return Task.CompletedTask;
            diagnostics.Write("application", "logs_exported");
            using var output = new ZipArchive(File.Create(dialog.FileName), ZipArchiveMode.Create);
            foreach (var file in Directory.EnumerateFiles(diagnostics.DirectoryPath, "app-*.jsonl*")) output.CreateEntryFromFile(file, Path.GetFileName(file));
            Status = "诊断日志已导出。日志不包含密钥、授权码或邮件正文。"; return Task.CompletedTask;
        });
        gmailAuth.Progress += stage => OnUi(() =>
        {
            diagnostics.Write("gmail", stage);
            GmailProgress = stage switch { "waiting_browser" => "正在等待浏览器授权…", "exchanging_code" => "正在确认授权…", "loading_account" => "正在读取邮箱账户…", _ => "正在连接 Gmail…" };
        });
        _ = gmailAuth.Account;
        if (gmailAuth.CredentialError != null) GmailProgress = Explain(gmailAuth.CredentialError);
    }

    public Task InitializeAsync() => initialization ??= InitializeCoreAsync();
    private async Task InitializeCoreAsync()
    {
        diagnostics.Write("application", "started");
        try { await EnsureAgentAsync(); } catch (Exception error) { ReportError(error); }
    }
    private async Task EnsureAgentAsync() { if (!ComponentReady) await ConnectAgentAsync(); }
    private async Task ConnectAgentAsync()
    {
        await connectionGate.WaitAsync(componentCancellation.Token);
        ComponentLoading = true; ComponentReady = false; ComponentStatus = "组件加载中 · 检查文件…";
        try
        {
            diagnostics.Write("component", "loading");
            if (bridge != null) { bridge.EventReceived -= OnAgentEvent; await bridge.DisposeAsync(); bridge = null; }
            if (!File.Exists(Settings.AgentHostPath)) throw new InvalidOperationException("COMPONENT_FILES_MISSING");
            Settings.Save();
            ComponentStatus = "组件加载中 · 初始化运行环境…";
            var dispatcher = new ToolDispatcher(store, library, outreach,
                new BraveWebSearch(http, () => File.Exists(Settings.SearchKeyFile) ? File.ReadAllText(Settings.SearchKeyFile).Trim() : null),
                new ScholarlySearch(new ArxivSearch(http), new CrossrefSearch(http)), web);
            bridge = new AgentBridge(Settings.NodePath, Settings.AgentHostPath, Path.Combine(DataDirectory, "pi"), dispatcher, diagnostics);
            bridge.EventReceived += OnAgentEvent;
            discovery = new DiscoveryService(database, store, library, bridge);
            discovery.Changed += () => OnUi(Refresh);
            ComponentStatus = "组件加载中 · 读取服务商与模型…";
            await RefreshAgentStatusAsync(componentCancellation.Token);
            ComponentReady = true; ComponentStatus = "组件已就绪"; Status = "组件加载完成，请选择模型服务商并连接账户。";
        }
        catch
        {
            ComponentStatus = "组件加载失败，可点击重试。";
            if (bridge != null) { bridge.EventReceived -= OnAgentEvent; await bridge.DisposeAsync(); bridge = null; }
            throw;
        }
        finally { ComponentLoading = false; connectionGate.Release(); }
    }
    private async Task RefreshAgentStatusAsync(CancellationToken token = default)
    {
        var response = await bridge!.RequestAsync(new { type = "status", id = NewId(), provider = Settings.Provider }, token);
        var data = response.GetProperty("data");
        var selected = Settings.Provider;
        Providers.Clear();
        foreach (var item in data.GetProperty("providers").EnumerateArray())
        {
            var id = item.GetProperty("id").GetString()!;
            var name = id switch { "openai-codex" => "OpenAI · ChatGPT 账户", "openai" => "OpenAI · API", "anthropic" => "Claude · Anthropic", "zai" => "GLM · Z.AI", "zai-coding-cn" => "GLM · 智谱中国", _ => UiText(item.GetProperty("name").GetString()!) };
            Providers.Add(new(id, name, item.GetProperty("configured").GetBoolean(), item.GetProperty("oauth").GetBoolean(), item.GetProperty("api_key").GetBoolean(),
                item.GetProperty("models").EnumerateArray().Select(model => model.GetProperty("id").GetString()!).ToArray()));
        }
        SelectedProvider = Providers.FirstOrDefault(item => item.Id == selected) ?? Providers.FirstOrDefault();
    }
    private async Task LoginAsync()
    {
        AuthBusy = true; authUrl = null; lastOpenedAuthUrl = null; ProviderStatus = "正在准备授权…"; ClearAuthPrompt();
        var provider = SelectedProvider!.Id; var method = SelectedLoginMethod!.Id;
        diagnostics.Write("auth", "started", provider);
        try
        {
            Settings.Save();
            var secret = ApiKeyRequired ? ProviderApiKey.Trim() : null;
            ProviderApiKey = "";
            object request = secret == null
                ? new { type = "login", id = NewId(), provider, auth_type = "oauth", method }
                : new { type = "login", id = NewId(), provider, auth_type = "api_key", method, secret };
            await bridge!.RequestAsync(request);
        }
        catch { AuthBusy = false; throw; }
    }
    private async Task ConnectGmailAsync(bool forceReauthorize = false)
    {
        mailBusy = true; gmailCancellation = new(); GmailProgress = "正在检查 Gmail 配置…"; CommandManager.InvalidateRequerySuggested();
        try
        {
            _ = GoogleClient.FromFile(Settings.GoogleClientFile);
            Settings.Save(); diagnostics.Write("gmail", "started");
            await gmailAuth.ConnectAsync(uri =>
            {
                gmailAuthUrl = uri.AbsoluteUri;
                CommandManager.InvalidateRequerySuggested();
                if (AutoOpenLoginBrowser) loginBrowser(uri);
                else GmailProgress = "登录链接已准备好，请点击复制链接，在选定的浏览器中打开。";
            }, gmailCancellation.Token, forceReauthorize);
            Raise(nameof(GmailStatus)); Raise(nameof(GmailConnectLabel)); Raise(nameof(IsSignedIn)); Raise(nameof(NeedsSignIn)); Raise(nameof(SignedInAccount)); Raise(nameof(AccountControlsEnabled));
            GmailProgress = "登录成功，此 Gmail 也是投递邮箱。"; Status = GmailProgress; diagnostics.Write("gmail", "connected");
        }
        catch (Exception error) { GmailProgress = error is OperationCanceledException ? "Gmail 授权已取消。" : Explain(DiagnosticLog.ErrorCode(error)); ReportError(error); }
        finally { mailBusy = false; gmailAuthUrl = null; gmailCancellation.Dispose(); gmailCancellation = null; CommandManager.InvalidateRequerySuggested(); }
    }
    private async Task CancelCurrentAsync()
    {
        taskCancellation?.Cancel(); gmailCancellation?.Cancel();
        if (bridge != null && (Busy || AuthBusy)) await bridge.CancelAsync();
    }
    private void ClearAuthPrompt()
    {
        authPromptId = null; AuthPrompt = ""; AuthReply = ""; AuthChoices.Clear(); SelectedAuthChoice = null;
        AuthSecret = false; Raise(nameof(AuthReply)); Raise(nameof(AuthText)); Raise(nameof(HasAuthChoices));
    }
    private void OnAgentEvent(JsonElement message) => OnUi(() =>
    {
        var type = message.GetProperty("type").GetString();
        diagnostics.Write("component", type ?? "event", message.TryGetProperty("code", out var code) && type != "auth_device_code" ? code.GetString() : null);
        switch (type)
        {
            case "auth_url":
            case "auth_device_code":
                authUrl = message.GetProperty("url").GetString();
                ProviderStatus = type == "auth_device_code" ? "请在授权页输入设备码：" + message.GetProperty("code").GetString() : "正在等待浏览器授权…";
                if (!AutoOpenLoginBrowser) ProviderStatus += " 请复制登录链接，在选定的浏览器中打开。";
                else if (authUrl != null && authUrl != lastOpenedAuthUrl)
                {
                    lastOpenedAuthUrl = authUrl;
                    try { loginBrowser(new Uri(authUrl)); } catch (Exception error) { diagnostics.Write("auth", "browser_failed", error: error); ProviderStatus += " 浏览器未能自动打开，请复制链接或重新打开授权页。"; }
                }
                break;
            case "auth_progress":
                if (message.TryGetProperty("stage", out var stage) && stage.GetString() == "verifying_credentials")
                    ProviderStatus = "已收到浏览器授权，正在验证并保存凭据…请以程序显示的结果为准。";
                break;
            case "auth_prompt":
                authPromptId = message.GetProperty("prompt_id").GetString();
                var kind = message.GetProperty("kind").GetString(); AuthSecret = kind == "secret";
                AuthChoices.Clear();
                if (message.TryGetProperty("options", out var options)) foreach (var option in options.EnumerateArray()) AuthChoices.Add(new(option.GetProperty("id").GetString()!, UiText(option.GetProperty("label").GetString()!)));
                SelectedAuthChoice = AuthChoices.FirstOrDefault(); Raise(nameof(SelectedAuthChoice)); Raise(nameof(AuthText)); Raise(nameof(HasAuthChoices));
                AuthPrompt = kind switch { "select" => "请选择授权选项：", "secret" => "请输入服务商要求的密钥：", "manual_code" => "若浏览器未自动返回，请粘贴授权后的完整回调地址或授权码：", _ => "请填写服务商要求的授权信息：" };
                break;
            case "auth_prompt_closed":
                if (message.GetProperty("prompt_id").GetString() == authPromptId) ClearAuthPrompt();
                break;
            case "auth_finished":
                authUrl = null; ClearAuthPrompt();
                if (message.GetProperty("ok").GetBoolean()) _ = FinishLoginAsync();
                else { AuthBusy = false; ProviderStatus = Explain(message.GetProperty("code").GetString()!); Status = ProviderStatus; }
                break;
            case "host_disconnected":
                ComponentReady = false; AuthBusy = false; ClearAuthPrompt();
                ComponentStatus = "组件连接已中断，请重试加载。"; break;
            default:
                Activity.Insert(0, DateTime.Now.ToString("HH:mm:ss") + " " + type);
                while (Activity.Count > 200) Activity.RemoveAt(Activity.Count - 1);
                break;
        }
        CommandManager.InvalidateRequerySuggested();
    });
    private async Task FinishLoginAsync()
    {
        try { await RefreshAgentStatusAsync(); Settings.Save(); ProviderStatus = "账户配置已保存，可以选择模型开始使用。"; Status = ProviderStatus; }
        catch (Exception error) { ReportError(error); }
        finally { AuthBusy = false; }
    }
    private void ReportError(Exception error)
    {
        var code = DiagnosticLog.ErrorCode(error);
        diagnostics.Write("application", "operation_failed", code, error);
        Status = error is OperationCanceledException ? "操作已取消。" : error is TimeoutException ? "连接超时，请检查网络后重试。" : Explain(code);
    }
    private static string UiText(string text) => Regex.Replace(text, @"\bPi\b", "组件", RegexOptions.IgnoreCase);
    private static string Explain(string code) => code switch
    {
        "RUN_BUSY" => "正在执行研究任务，请等待完成或取消后再操作。",
        "AUTH_IN_PROGRESS" => "授权正在进行，请完成浏览器步骤，或先取消本次授权。",
        "AUTH_CANCELLED" => "授权已取消或超时，可以重新连接。",
        "AUTH_CALLBACK_PORT_BUSY" => "授权回调端口已被占用，请改用设备码授权，或关闭其他授权窗口后重试。",
        "AUTH_NETWORK_FAILED" => "浏览器授权后的令牌交换或连接失败，请检查网络和系统代理后重试；浏览器显示成功不代表凭据已保存。",
        "AUTH_CREDENTIAL_SAVE_FAILED" => "授权已完成，但凭据保存失败，请检查本机资料目录的写入权限。",
        "AUTH_REJECTED" => "服务商拒绝授权，请检查账户权限并重新连接。",
        "AUTH_FAILED" => "授权未完成，请重试或改用其他授权方式；诊断事件已写入日志。",
        "COMPONENT_FILES_MISSING" => "组件文件不完整，请重新安装到 Windows 本地目录。",
        "GMAIL_CLIENT_NOT_CONFIGURED" => "此安装包缺少登录配置，请下载官方最新版或联系发布者。",
        "GMAIL_CLIENT_FILE_MISSING" => "找不到 Gmail 客户端配置，请重新导入 JSON 文件。",
        "GMAIL_CLIENT_INVALID" => "Google 客户端 JSON 无效，请导入桌面应用类型的客户端配置。",
        "GMAIL_CLIENT_NOT_DESKTOP" => "此配置不是 Google 桌面应用客户端，请使用 Desktop app 类型。",
        "GMAIL_CREDENTIAL_UNREADABLE" => "保存的 Gmail 凭据无法读取，请重新连接 Gmail。",
        "GMAIL_AUTH_TIMEOUT" => "Gmail 授权等待超时，请重新连接。",
        "GMAIL_AUTH_DENIED" => "Google 拒绝了授权，请检查应用发布与验证状态，以及账号是否允许访问。",
        "GMAIL_REQUIRED_SCOPES_MISSING" => "Gmail 未授予所需权限，请重新连接并允许发送和读取邮件。",
        "GMAIL_REFRESH_TOKEN_REQUIRED" => "Google 未返回离线授权，请重新连接并确认授权。",
        "GMAIL_OAUTH_INVALID_CLIENT" => "Google 拒绝客户端配置，请重新导入正确的桌面应用 OAuth JSON。",
        "GMAIL_OAUTH_INVALID_GRANT" => "Google 授权已失效，请重新连接。",
        "GMAIL_API_DISABLED" => "请在对应 Google Cloud 项目启用 Gmail API 后重新连接。",
        "NETWORK_FAILED" => "网络连接失败，请检查网络或代理设置后重试。",
        _ => "操作未完成，请检查配置与网络，并查看诊断日志。"
    };
}
