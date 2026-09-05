using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using ToolsTouch.Core;

namespace ToolsTouch.Desktop;

public sealed class MainViewModel : Observable, IAsyncDisposable
{
    private readonly LocalDatabase database;
    private readonly ResearchStore store;
    private readonly LibraryService library;
    private readonly OutreachService outreach;
    private readonly GmailAuth gmailAuth;
    private readonly GmailService gmail;
    private CancellationTokenSource? gmailCancellation;
    private bool mailBusy;
    private readonly PublicWeb web = new();
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(40) };
    private AgentBridge? bridge;
    private DiscoveryService? discovery;
    private CancellationTokenSource? taskCancellation;
    private string? authPromptId;
    private string? authUrl;
    private string? profileId;
    private bool refreshing;
    private bool disposed;
    public DesktopSettings Settings { get; } = DesktopSettings.Load();
    public string DataDirectory => DesktopSettings.DataDirectory;
    public string GmailStatus => gmailAuth.Account == null ? "Gmail 未连接" : "Gmail · " + gmailAuth.Account;
    public ObservableCollection<Professor> Professors { get; } = [];
    public ObservableCollection<Paper> Papers { get; } = [];
    public ObservableCollection<OutreachHistory> ProfessorHistory { get; } = [];
    public ObservableCollection<Draft> Drafts { get; } = [];
    public ObservableCollection<AgentRun> Runs { get; } = [];
    public ObservableCollection<string> Models { get; } = [];
    public ObservableCollection<string> Activity { get; } = [];
    private string status = "就绪。请在 Settings 配置 AgentHost 并连接 OpenAI。";
    public string Status { get => status; private set => Set(ref status, value); }
    private string openAiStatus = "尚未读取 Pi 状态";
    public string OpenAiStatus { get => openAiStatus; private set => Set(ref openAiStatus, value); }
    private string authPrompt = "";
    public string AuthPrompt { get => authPrompt; private set => Set(ref authPrompt, value); }
    public string AuthReply { get; set; } = "";
    public string Query { get; set; } = "帮我找做 World Model 的老师";
    public string ProfessorFilter { get; set; } = "";
    private int selectedPage;
    public int SelectedPage { get => selectedPage; set => Set(ref selectedPage, value); }
    private bool busy;
    public bool Busy { get => busy; private set { Set(ref busy, value); CommandManager.InvalidateRequerySuggested(); } }
    private Professor? selectedProfessor;
    public Professor? SelectedProfessor
    {
        get => selectedProfessor;
        set { if (Set(ref selectedProfessor, value) && value != null && !refreshing) LoadProfessor(); }
    }
    public Paper? SelectedPaper { get; set; }
    public OutreachHistory? SelectedHistory { get; set; }
    public int PaperStartPage { get; set; } = 1;
    public AgentRun? SelectedRun { get; set; }
    private Draft? selectedDraft;
    public Draft? SelectedDraft
    {
        get => selectedDraft;
        set
        {
            if (!Set(ref selectedDraft, value) || value == null || refreshing) return;
            Raise(nameof(DraftLocked));
            Recipient = value.Recipient; Subject = value.Subject; Body = value.Body; Attachment = value.CvPath ?? "";
            Raise(nameof(Recipient)); Raise(nameof(Subject)); Raise(nameof(Body)); Raise(nameof(Attachment));
        }
    }
    public string Recipient { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string Attachment { get; private set; } = "";
    public bool DraftLocked => !CanEditDraft();
    private string analysis = "选择导师后查看研究分析。";
    public string Analysis { get => analysis; private set => Set(ref analysis, value); }
    private string paperText = "选择论文后点击阅读。";
    public string PaperText { get => paperText; private set => Set(ref paperText, value); }
    private string cvText = "尚未导入 CV";
    public string CvText { get => cvText; private set => Set(ref cvText, value); }
    public string Interests { get; set; } = "";
    public string Projects { get; set; } = "";
    public string Skills { get; set; } = "";
    public string Summary => $"导师 {Professors.Count} · 待编辑 {Drafts.Count(d => d.State == "Draft")} · 已发送 {Drafts.Count(d => d.State == "Sent")} · 已回复 {outreach.ReplyCount()} · 待恢复 {Runs.Count(r => r.State is "Partial" or "Failed" or "Cancelled")}";
    public ICommand RefreshCommand { get; }
    public ICommand ConnectAgentCommand { get; }
    public ICommand LoginCommand { get; }
    public ICommand OpenLoginCommand { get; }
    public ICommand AuthReplyCommand { get; }
    public ICommand DiscoverCommand { get; }
    public ICommand AnalyzeCommand { get; }
    public ICommand GenerateDraftCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ViewProfessorCommand { get; }
    public ICommand OpenHomepageCommand { get; }
    public ICommand ReadPaperCommand { get; }
    public ICommand OpenPaperSourceCommand { get; }
    public ICommand OpenHistoryDraftCommand { get; }
    public ICommand ImportCvCommand { get; }
    public ICommand ConfirmCvCommand { get; }
    public ICommand SaveDraftCommand { get; }
    public ICommand AttachCvCommand { get; }
    public ICommand RemoveAttachmentCommand { get; }
    public ICommand SaveSettingsCommand { get; }
    public ICommand ConnectGmailCommand { get; }
    public ICommand SendCommand { get; }
    public ICommand ReconcileCommand { get; }
    public ICommand SyncRepliesCommand { get; }

    public MainViewModel()
    {
        Directory.CreateDirectory(DataDirectory);
        database = new(Path.Combine(DataDirectory, "tools-touch.db")); database.Initialize();
        store = new(database); library = new(database, DataDirectory, web); outreach = new(database);
        gmailAuth = new GmailAuth(http, new WindowsSecretStore(), () => GoogleClient.FromFile(Settings.GoogleClientFile));
        gmail = new GmailService(http, gmailAuth);
        store.RecoverInterruptedRuns(); outreach.RecoverInterruptedSends();
        UiCommand Command(Func<Task> action, Func<bool>? enabled = null) => new(action, ReportError, enabled);
        RefreshCommand = Command(() => { Refresh(); return Task.CompletedTask; });
        ConnectAgentCommand = Command(ConnectAgentAsync, () => !Busy);
        LoginCommand = Command(async () => { await EnsureAgentAsync(); await bridge!.RequestAsync(new { type = "login", id = NewId() }); }, () => !Busy);
        OpenLoginCommand = Command(() => { if (authUrl != null) OpenUrl(authUrl); return Task.CompletedTask; }, () => authUrl != null);
        AuthReplyCommand = Command(async () => { await bridge!.RequestAsync(new { type = "auth_reply", id = NewId(), prompt_id = authPromptId, value = AuthReply }); AuthReply = ""; Raise(nameof(AuthReply)); }, () => authPromptId != null);
        DiscoverCommand = Command(() => StartTaskAsync("Discover"), () => !Busy && !string.IsNullOrWhiteSpace(Query));
        AnalyzeCommand = Command(() => StartTaskAsync("Analyze"), () => !Busy && SelectedProfessor != null);
        GenerateDraftCommand = Command(() => StartTaskAsync("Draft"), () => !Busy && SelectedProfessor != null);
        ResumeCommand = Command(async () => { await EnsureAgentAsync(); await ExecuteTaskAsync(SelectedRun!.Id); }, () => !Busy && SelectedRun?.State is "Queued" or "Partial" or "Failed" or "Cancelled");
        CancelCommand = Command(async () => { taskCancellation?.Cancel(); gmailCancellation?.Cancel(); if (bridge != null) await bridge.CancelAsync(); });
        ViewProfessorCommand = Command(() => { LoadProfessor(); SelectedPage = 3; return Task.CompletedTask; }, () => SelectedProfessor != null);
        OpenHomepageCommand = Command(() => { OpenUrl(SelectedProfessor!.Homepage); return Task.CompletedTask; }, () => SelectedProfessor != null);
        ReadPaperCommand = Command(async () =>
        {
            var paper = SelectedPaper!;
            PaperText = "正在获取论文…";
            var startPage = PaperStartPage;
            var reading = await Task.Run(() => library.ReadPaperAsync(paper.Id, startPage, 10, CancellationToken.None));
            PaperText = $"{paper.Title}\n{reading.Coverage}\n{reading.SourceUrl}\n{reading.Note}\n\n" + string.Join("\n\n", reading.Pages.Select(page => $"页 {page.Page}\n{page.Text}"));
        }, () => SelectedPaper != null);
        OpenPaperSourceCommand = Command(() => { OpenUrl(SelectedPaper!.SourceUrl); return Task.CompletedTask; }, () => SelectedPaper != null);
        OpenHistoryDraftCommand = Command(() => { SelectedDraft = outreach.Get(SelectedHistory!.Id); SelectedPage = 4; return Task.CompletedTask; }, () => SelectedHistory != null);
        ImportCvCommand = Command(ImportCvAsync, () => !Busy);
        ConfirmCvCommand = Command(() =>
        {
            var profile = library.ConfirmProfile(profileId!, Interests, Projects, Skills);
            profileId = profile.Id; CvText = $"已确认 CV 资料版本 {profile.Version}\n{profile.CvPath}";
            return Task.CompletedTask;
        }, () => profileId != null && !Busy);
        SaveDraftCommand = Command(() => { SaveDraft(); return Task.CompletedTask; }, CanEditDraft);
        AttachCvCommand = Command(() =>
        {
            var profile = library.GetProfile(confirmedOnly: true) ?? throw new InvalidOperationException("请先导入并确认 CV。");
            Attachment = library.VerifyCv(profile);
            Raise(nameof(Attachment)); return Task.CompletedTask;
        }, CanEditDraft);
        RemoveAttachmentCommand = Command(() => { Attachment = ""; Raise(nameof(Attachment)); return Task.CompletedTask; }, CanEditDraft);
        SaveSettingsCommand = Command(() => { Settings.Save(); Status = "设置已保存。Agent 路径更改后请重新连接。"; return Task.CompletedTask; }, () => !Busy);
        ConnectGmailCommand = Command(async () =>
        {
            mailBusy = true; gmailCancellation = new(); CommandManager.InvalidateRequerySuggested();
            try { Settings.Save(); await gmailAuth.ConnectAsync(uri => OpenUrl(uri.AbsoluteUri), gmailCancellation.Token); Raise(nameof(GmailStatus)); Status = "Gmail 已连接。"; }
            finally { mailBusy = false; gmailCancellation.Dispose(); gmailCancellation = null; CommandManager.InvalidateRequerySuggested(); }
        }, () => !Busy && !mailBusy);
        SendCommand = Command(SendAsync, () => CanEditDraft() && gmail.Account != null && !mailBusy);
        ReconcileCommand = Command(async () =>
        {
            mailBusy = true;
            try
            {
                var id = SelectedDraft!.Id;
                Status = await outreach.ReconcileAsync(id, gmail) ? "已在 Gmail 已发送邮件中核实。" : "尚未找到对应邮件，保留结果不确定状态，不会重发。";
                SelectedDraft = outreach.Get(id); Refresh();
            }
            finally { mailBusy = false; }
        }, () => SelectedDraft?.State == "Unknown" && gmail.Account != null && !mailBusy);
        SyncRepliesCommand = Command(async () =>
        {
            mailBusy = true;
            try { var count = await outreach.SyncRepliesAsync(gmail); Refresh(); Status = $"同步完成，{count} 个会话有回复。"; }
            finally { mailBusy = false; }
        }, () => gmail.Account != null && !mailBusy);
        Refresh();
        var current = library.GetProfile();
        if (current != null) LoadProfile(current);
    }

    private bool CanEditDraft() => SelectedDraft?.State is "Draft" or "Failed";
    private static string NewId() => Guid.NewGuid().ToString("N");
    private void ReportError(Exception error) => Status = error is OperationCanceledException ? "任务已取消，已保存结果保留。" : "操作失败：" + error.Message;
    private static void OpenUrl(string url)
    {
        var uri = PublicWeb.ValidateUri(url);
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private async Task EnsureAgentAsync() { if (bridge == null) await ConnectAgentAsync(); }
    private async Task ConnectAgentAsync()
    {
        Settings.Save();
        if (bridge != null) { bridge.EventReceived -= OnAgentEvent; await bridge.DisposeAsync(); bridge = null; }
        var dispatcher = new ToolDispatcher(store, library, outreach,
            new BraveWebSearch(http, () => File.Exists(Settings.SearchKeyFile) ? File.ReadAllText(Settings.SearchKeyFile).Trim() : null),
            new ScholarlySearch(new ArxivSearch(http), new CrossrefSearch(http)), web);
        bridge = new AgentBridge(Settings.NodePath, Settings.AgentHostPath, Path.Combine(DataDirectory, "pi"), dispatcher);
        bridge.EventReceived += OnAgentEvent;
        discovery = new DiscoveryService(database, store, library, bridge);
        discovery.Changed += () => OnUi(Refresh);
        await RefreshAgentStatusAsync();
        Status = "Pi 已启动。";
    }
    private async Task RefreshAgentStatusAsync()
    {
        var response = await bridge!.RequestAsync(new { type = "status", id = NewId() });
        var data = response.GetProperty("data");
        OpenAiStatus = data.GetProperty("configured").GetBoolean() ? "Pi 已保存 OpenAI 登录凭据（调用可用性待请求验证）" : "OpenAI 未连接";
        Models.Clear();
        foreach (var item in data.GetProperty("models").EnumerateArray()) Models.Add(item.GetProperty("id").GetString()!);
        if (!Models.Contains(Settings.Model)) Settings.Model = Models.FirstOrDefault() ?? "";
        Raise(nameof(Settings));
    }
    private void OnUi(Action action)
    {
        if (!disposed && !Application.Current.Dispatcher.HasShutdownStarted) Application.Current.Dispatcher.BeginInvoke(action);
    }
    private void OnAgentEvent(JsonElement message) => OnUi(() =>
    {
        var type = message.GetProperty("type").GetString();
        switch (type)
        {
            case "auth_url": authUrl = message.GetProperty("url").GetString(); Status = "点击“打开登录浏览器”继续 OpenAI 授权。"; break;
            case "auth_progress": Status = message.GetProperty("message").GetString()!; break;
            case "auth_prompt":
                authPromptId = message.GetProperty("prompt_id").GetString(); AuthPrompt = message.GetProperty("message").GetString()!;
                if (message.TryGetProperty("options", out var options)) AuthPrompt += "\n" + string.Join("\n", options.EnumerateArray().Select(option => option.GetProperty("id").GetString() + ": " + option.GetProperty("label").GetString()));
                break;
            case "auth_prompt_closed": authPromptId = null; AuthPrompt = ""; break;
            case "auth_finished":
                authPromptId = null; authUrl = null; AuthPrompt = "";
                Status = message.GetProperty("ok").GetBoolean() ? "OpenAI 登录完成。点击读取状态刷新模型。" : "OpenAI 登录未完成，可重新连接。";
                break;
            case "host_disconnected": OpenAiStatus = "Pi 进程已断开，请重新连接。"; break;
            default:
                Activity.Insert(0, DateTime.Now.ToString("HH:mm:ss") + " " + type +
                    (message.TryGetProperty("data", out var data) && data.TryGetProperty("tool", out var tool) ? " · " + tool.GetString() : ""));
                while (Activity.Count > 200) Activity.RemoveAt(Activity.Count - 1);
                break;
        }
        CommandManager.InvalidateRequerySuggested();
    });

    private async Task StartTaskAsync(string kind)
    {
        await EnsureAgentAsync();
        var run = discovery!.Create(kind, Query, kind == "Discover" ? null : SelectedProfessor!.Id, Settings.MaxToolCalls, checked(Settings.StageTimeoutSeconds * 1000));
        await ExecuteTaskAsync(run.Id);
        if (kind == "Draft") SelectedPage = 4;
    }
    private async Task ExecuteTaskAsync(string runId)
    {
        Busy = true;
        taskCancellation = new();
        try { await discovery!.ExecuteAsync(runId, Settings.Model, taskCancellation.Token); Status = "任务已完成。"; }
        finally { Busy = false; taskCancellation.Dispose(); taskCancellation = null; Refresh(); if (SelectedProfessor != null) LoadProfessor(); }
    }
    private void Refresh()
    {
        var professorId = SelectedProfessor?.Id;
        var draft = SelectedDraft;
        var runId = SelectedRun?.Id;
        refreshing = true;
        try
        {
            Replace(Professors, store.SearchProfessors(ProfessorFilter));
            // Keep an editor's loaded revision until explicit save/reload; stale saves fail optimistically.
            Replace(Drafts, outreach.List().Select(item => item.Id == draft?.Id ? draft : item)); Replace(Runs, store.ListRuns());
            SelectedProfessor = Professors.FirstOrDefault(item => item.Id == professorId);
            SelectedDraft = Drafts.FirstOrDefault(item => item.Id == draft?.Id);
            SelectedRun = Runs.FirstOrDefault(item => item.Id == runId);
            Raise(nameof(Summary));
        }
        finally { refreshing = false; }
        if (SelectedProfessor != null) LoadProfessor();
    }
    private void LoadProfessor()
    {
        if (SelectedProfessor == null) return;
        Replace(Papers, library.ListPapers(SelectedProfessor.Id));
        Replace(ProfessorHistory, outreach.History(SelectedProfessor.Id));
        var json = discovery?.LatestAnalysis(SelectedProfessor.Id);
        if (json == null)
        {
            using var connection = database.Open();
            using var command = LocalDatabase.Command(connection, "SELECT ContentJson FROM ProfessorAnalysis WHERE ProfessorId=$id ORDER BY CreatedAt DESC LIMIT 1", ("$id", SelectedProfessor.Id));
            json = command.ExecuteScalar() as string;
        }
        Analysis = json == null ? "尚无分析。点击深入分析开始。" : FormatAnalysis(json);
    }
    private static string FormatAnalysis(string json)
    {
        var value = JsonSerializer.Deserialize<JsonElement>(json);
        return value.GetProperty("research_summary").GetString() + "\n\n个人匹配\n" +
            (value.GetProperty("personal_match").ValueKind == JsonValueKind.Null ? "没有可用的个人匹配结论。" : value.GetProperty("personal_match").GetString()) +
            "\n\n来源\n" + string.Join("\n", value.GetProperty("evidence").EnumerateArray().Select(item => item.GetProperty("claim").GetString() + "\n" + item.GetProperty("url").GetString()));
    }
    private async Task ImportCvAsync()
    {
        var dialog = new OpenFileDialog { Filter = "PDF CV|*.pdf", Title = "选择 CV" };
        if (dialog.ShowDialog() != true) return;
        LoadProfile(await Task.Run(() => library.ImportCv(dialog.FileName)));
    }
    private void LoadProfile(UserProfile profile)
    {
        profileId = profile.Id;
        var json = JsonSerializer.Deserialize<JsonElement>(profile.ExperiencesJson);
        CvText = $"CV 版本 {profile.Version} · {(profile.Confirmed ? "已确认" : "请核对并填写经历后确认")}\n" +
            (json.TryGetProperty("extractedText", out var text) ? text.GetString() : "");
        Interests = json.TryGetProperty("researchInterests", out var interests) ? interests.GetString()! : "";
        Projects = json.TryGetProperty("projects", out var projects) ? projects.GetString()! : "";
        Skills = json.TryGetProperty("skills", out var skills) ? skills.GetString()! : "";
        Raise(nameof(Interests)); Raise(nameof(Projects)); Raise(nameof(Skills));
    }
    private void SaveDraft()
    {
        var saved = outreach.Edit(SelectedDraft!.Id, SelectedDraft.Revision, Recipient, Subject, Body, string.IsNullOrEmpty(Attachment) ? null : Attachment);
        SelectedDraft = saved; Status = "草稿已保存。";
    }
    private async Task SendAsync()
    {
        mailBusy = true; CommandManager.InvalidateRequerySuggested();
        try
        {
            SaveDraft();
            var reviewed = SelectedDraft!;
            if (MessageBox.Show($"从：{gmail.Account}\n收件人：{reviewed.Recipient}\n主题：{reviewed.Subject}\nCV：{(reviewed.CvPath == null ? "无附件" : Path.GetFileName(reviewed.CvPath))}\n\n确认发送当前已保存的正文与附件？", "确认发送邮件", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            try
            {
                await outreach.SendAsync(reviewed.Id, gmail, senderAccount: gmail.Account, expectedRevision: reviewed.Revision);
                Status = "Gmail 已接受发送请求。";
            }
            finally { SelectedDraft = outreach.Get(reviewed.Id); Refresh(); }
        }
        finally { mailBusy = false; CommandManager.InvalidateRequerySuggested(); }
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        var next = values.ToArray();
        for (var i = target.Count - 1; i >= 0; i--) if (!next.Contains(target[i])) target.RemoveAt(i);
        for (var i = 0; i < next.Length; i++)
        {
            var existing = target.IndexOf(next[i]);
            if (existing < 0) target.Insert(i, next[i]); else if (existing != i) target.Move(existing, i);
        }
    }
    public async ValueTask DisposeAsync()
    {
        disposed = true; taskCancellation?.Cancel(); gmailCancellation?.Cancel();
        if (bridge != null) await bridge.DisposeAsync().ConfigureAwait(false);
        web.Dispose(); http.Dispose();
    }
}
