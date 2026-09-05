using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Input;
using Microsoft.Data.Sqlite;
using ToolsTouch.Application;
using ToolsTouch.Core;
using ToolsTouch.Desktop;
using ToolsTouch.Infrastructure.Recommendation;

internal static class Program
{
    private const string TestSecret = "synthetic-credential-测试-only";
    private static readonly List<object> Results = [];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--read-secret")
            return new WindowsSecretStore(args[1]).Read("gmail") == TestSecret ? 0 : 1;

        string Option(string name) => args.SkipWhile(value => value != name).Skip(1).FirstOrDefault()
            ?? throw new ArgumentException("Missing " + name);
        var output = Path.GetFullPath(Option("--output"));
        var host = Path.GetFullPath(Option("--host"));
        var node = Path.GetFullPath(Option("--node"));
        Directory.CreateDirectory(output);
        var directory = Path.Combine(Path.GetTempPath(), "tools-touch-desktop-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        // Initialize compiled production resources (including merged dictionaries).
        // Do not call Run: OnStartup would open the real user workspace.
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        MainViewModel? model = null;
        MainWindow? window = null;
        var failures = 0;
        void Test(string name, Action action)
        {
            try { action(); Results.Add(new { name, passed = true }); Console.WriteLine("PASS: " + name); }
            catch (Exception error) { failures++; Results.Add(new { name, passed = false, error = error.ToString() }); Console.WriteLine("FAIL: " + name + " · " + error.Message); }
        }
        try
        {
            Test("DPAPI persistence, replacement, tamper detection and deletion", () =>
            {
                var secrets = new WindowsSecretStore(directory);
                Check(secrets.Read("gmail") == null, "fresh store must be empty");
                secrets.Write("gmail", "old synthetic value");
                secrets.Write("gmail", TestSecret);
                var path = Path.Combine(directory, "credentials", "gmail.dpapi");
                Check(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(TestSecret), "plaintext credential on disk");
                var process = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                // dotnet run uses an apphost; also support execution as dotnet <assembly>.
                if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                    process.ArgumentList.Add(typeof(Program).Assembly.Location);
                process.ArgumentList.Add("--read-secret"); process.ArgumentList.Add(directory);
                using var child = Process.Start(process)!;
                if (!child.WaitForExit(15000)) { child.Kill(true); throw new Exception("DPAPI child timed out"); }
                Check(child.ExitCode == 0, "a fresh Windows process must decrypt the saved value");
                File.WriteAllBytes(path, [1, 2, 3]);
                try { secrets.Read("gmail"); throw new Exception("damaged credentials accepted"); }
                catch (CryptographicException) { }
                secrets.Delete("gmail");
                Check(secrets.Read("gmail") == null, "credential deletion failed");
            });

            var settings = DesktopSettings.Load(directory);
            settings.NodePath = node; settings.AgentHostPath = host; settings.Save();
            Test("isolated settings round trip", () =>
            {
                Check(DesktopSettings.Load(directory).AgentHostPath == host, "settings did not persist");
                var bundled = Path.Combine(AppContext.BaseDirectory, "config", "google-client.json");
                if (File.Exists(bundled))
                {
                    Check(DesktopSettings.Load(directory).GoogleClientFile == bundled, "fresh installation did not discover publisher login configuration");
                    _ = GoogleClient.FromFile(bundled);
                }
                Check(!File.ReadAllText(Path.Combine(directory, "settings.json")).Contains("storageDirectory"), "storage location must not be serialized");
            });
            var database = new LocalDatabase(Path.Combine(directory, "tools-touch.db")); database.Initialize();
            var store = new ResearchStore(database);
            var professor = store.SaveProfessor("测试导师", "测试大学", "https://example.org/professor", "test@example.org",
                [new("https://example.org/professor", "用于本地界面验证的合成资料")], "test-professor");
            var outreach = new OutreachService(database);
            var draft = outreach.CreateDraft(professor.Id, "test@example.org", "研究交流 · 测试草稿", "这是一封用于界面验证的本地草稿。", null, "test-draft");
            var sent = outreach.CreateDraft(professor.Id, "test@example.org", "已发送状态 · 替身记录", "测试替身，没有发送邮件。", null, "test-sent");
            outreach.SendAsync(sent.Id, new TestTransport()).GetAwaiter().GetResult();
            Execute(database, "INSERT INTO UserProfile(Id,Version,CvPath,CvHash,ExperiencesJson,Confirmed,CreatedAt) VALUES('desktop-profile',1,'synthetic.pdf','synthetic-hash','{}',1,'now')");
            var recommendationRepository = new RecommendationRepository(database, new ArtifactStore(database.ArtifactDirectory));
            var recommendationRanker = new Ranker();
            var recommendationCandidate = new RankingCandidate("ProfessorAppointment", professor.Id,
                new EligibilityResult(EligibilityState.Eligible, [], []),
                [new RankingComponent("direction", 1m, 0.5m, ["desktop-evidence"], null, "Rule")], 3,
                ["方向证据已由语义评估核验"], []);
            var baseRecommendationResult = recommendationRanker.Rank([recommendationCandidate], new WeightProfile("desktop-rank-v1"));
            var semanticRecommendationResult = recommendationRanker.Rank([recommendationCandidate with
            {
                Components = [new RankingComponent("direction", 1m, 0.75m, ["desktop-evidence"], null, "Semantic")]
            }], new WeightProfile("desktop-rank-v1"));
            recommendationRepository.Publish(new RecommendationRunInput("desktop-base-run", "Base", "desktop-profile", null, 2026,
                "desktop-rank-v1", "{\"minimumCoverage\":0.6}", "{\"candidate_scope_id\":\"desktop-scope-base\",\"targets\":[\"" + professor.Id + "\"]}",
                new OrganizationRepository(database).Get().DataRevision, DateTimeOffset.UtcNow), baseRecommendationResult);
            recommendationRepository.Publish(new RecommendationRunInput("desktop-semantic-run", "Semantic", "desktop-profile", null, 2026,
                "desktop-rank-v1", "{\"minimumCoverage\":0.6}", "{\"candidate_scope_id\":\"desktop-scope-semantic\",\"targets\":[\"" + professor.Id + "\"]}",
                new OrganizationRepository(database).Get().DataRevision, DateTimeOffset.UtcNow), semanticRecommendationResult);
            Task? googleCallback = null;
            var browserOpens = 0;
            string? copiedUrl = null;
            model = new MainViewModel(directory, new HttpClient(new GoogleHandler()), uri =>
            {
                browserOpens++;
                var query = uri.Query[1..].Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
                googleCallback = Task.Run(async () =>
                {
                    using var callback = new HttpClient();
                    using var response = await callback.GetAsync(query["redirect_uri"] + "?code=synthetic-code&state=" + query["state"]);
                    Check(response.IsSuccessStatusCode, "Google callback failed");
                });
            }, url => copiedUrl = url);
            window = new MainWindow { DataContext = model, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
            using var bindings = new BindingErrors();
            PresentationTraceSources.DataBindingSource.Listeners.Add(bindings);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            window.Show(); Pump();
            var tabs = (TabControl)window.FindName("WorkspaceTabs");
            Test("first launch requires Gmail; failed login cannot unlock workspace", () =>
            {
                if (File.Exists(Path.Combine(AppContext.BaseDirectory, "config", "google-client.json")))
                    Check(model.GoogleClientBundled && !model.ShowGoogleSetup, "public package asks end users to import OAuth configuration");
                Check(model.NeedsSignIn && !model.IsSignedIn && !model.DiscoverCommand.CanExecute(null), "first launch bypassed login");
                Check(((FrameworkElement)window.FindName("WorkspacePanel")).Visibility == Visibility.Collapsed, "workspace visible before login");
                Snapshot(window, Path.Combine(output, "first-launch-login.png"));
                model.Settings.GoogleClientFile = "";
                model.ConnectGmailCommand.Execute(null); Pump();
                Check(!model.IsSignedIn && model.GmailProgress.Contains("登录配置"), "missing config unlocked workspace");
            });
            Test("Gmail login unlocks app, identifies sender and survives reopening", () =>
            {
                var config = Path.Combine(directory, "google-client.json");
                File.WriteAllText(config, "{\"installed\":{\"client_id\":\"synthetic.apps.googleusercontent.com\",\"client_secret\":\"synthetic\"}}");
                model.Settings.GoogleClientFile = config;
                model.ConnectGmailCommand.Execute(null);
                WaitFor(() => model.IsSignedIn, 15); Pump();
                googleCallback!.GetAwaiter().GetResult();
                Check(model.SignedInAccount == "sender@example.org" && model.GmailStatus.Contains("sender@example.org"), "app account differs from sender");
                Check(((FrameworkElement)window.FindName("WorkspacePanel")).Visibility == Visibility.Visible, "login did not reveal workspace");
                var restored = new GmailAuth(new HttpClient(), new WindowsSecretStore(directory), () => GoogleClient.FromFile(config));
                Check(restored.Account == model.SignedInAccount, "login did not persist in protected storage");
                model.ConnectGmailCommand.Execute(null); Pump();
                Check(browserOpens == 1 && !model.ConnectGmailCommand.CanExecute(null), "already connected Gmail opens authorization again");
            });
            Test("window automatically loads components and provider catalogue", () =>
            {
                var startup = model.InitializeAsync();
                Check(ReferenceEquals(startup, model.InitializeAsync()), "startup must be idempotent");
                WaitFor(() => startup.IsCompleted, 100);
                Check(model.ComponentReady && !model.ComponentLoading, "automatic startup failed: " + model.Status);
                foreach (var provider in new[] { "openai-codex", "openai", "deepseek", "anthropic", "zai", "zai-coding-cn" })
                    Check(model.Providers.Any(item => item.Id == provider), "missing provider: " + provider);
            });
            Test("native API key configuration, duplicate prevention and log redaction", () =>
            {
                tabs.SelectedIndex = 5; Pump();
                model.SelectedProvider = model.Providers.Single(item => item.Id == "deepseek"); Pump();
                Check(model.ApiKeyRequired && !model.LoginCommand.CanExecute(null), "empty API key accepted");
                var key = (PasswordBox)window.FindName("ProviderKeyBox");
                key.Password = "synthetic-native-test-key"; Pump();
                Check(model.LoginCommand.CanExecute(null), "native password entry did not enable login");
                model.LoginCommand.Execute(null);
                Check(model.AuthBusy && !model.LoginCommand.CanExecute(null) && !model.AccountControlsEnabled, "duplicate login not disabled");
                WaitFor(() => !model.AuthBusy, 15);
                Check(model.SelectedProvider?.Configured == true, "provider credential not saved: " + model.Status);
                Check(key.Password.Length == 0 && model.ProviderApiKey.Length == 0, "API key remained visible in input");
                Check(!File.ReadAllText(Path.Combine(directory, "settings.json")).Contains("synthetic-native-test-key"), "API key leaked into settings");
                Check(!string.Join("", Directory.GetFiles(Path.Combine(directory, "logs")).Select(File.ReadAllText)).Contains("synthetic-native-test-key"), "API key leaked into logs");
                Snapshot(window, Path.Combine(output, "accounts-api-key.png"));
            });
            Test("missing Gmail configuration is explained and can be retried", () =>
            {
                model.Settings.GoogleClientFile = "";
                model.ReauthorizeGmailCommand.Execute(null); Pump();
                Check(model.GmailProgress.Contains("登录配置") && !model.GmailProgress.Contains("RUN_BUSY"), "missing configuration not explained");
                Check(model.ReauthorizeGmailCommand.CanExecute(null), "failed Gmail login blocks retry");
            });

            Test("manual browser mode copies the current model URL without opening a browser", () =>
            {
                model.AutoOpenLoginBrowser = false;
                var receive = typeof(MainViewModel).GetMethod("OnAgentEvent", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                typeof(MainViewModel).GetProperty("AuthBusy")!.SetValue(model, true);
                receive.Invoke(model, [JsonSerializer.SerializeToElement(new { type = "auth_url", url = "https://example.org/authorize?state=synthetic" })]); Pump();
                model.CopyLoginUrlCommand.Execute(null); Pump();
                Check(browserOpens == 1 && copiedUrl == "https://example.org/authorize?state=synthetic", "copy-only mode launched a browser or copied the wrong URL");
                receive.Invoke(model, [JsonSerializer.SerializeToElement(new { type = "auth_finished", ok = false, code = "AUTH_NETWORK_FAILED" })]); Pump();
                Check(!model.CopyLoginUrlCommand.CanExecute(null), "expired login URL can still be copied");
                Check(!DesktopSettings.Load(directory).AutoOpenLoginBrowser, "manual browser preference did not persist");
                model.AutoOpenLoginBrowser = true;
            });

            Test("eleven pages and detail tabs render without binding errors", () =>
            {
                string[] expectedPages = ["Dashboard", "Discover", "Professors", "Professor Detail", "Outreach",
                    "Applications", "Records", "Settings", "Admissions", "Faculty", "Recommendations"];
                Check(tabs.Items.Cast<TabItem>().Select(tab => tab.Header.ToString()).SequenceEqual(expectedPages),
                    "workspace navigation does not expose all eleven pages");
                var firstGrid = Descendants<DataGrid>(window).First();
                Check(firstGrid.EnableRowVirtualization && firstGrid.EnableColumnVirtualization &&
                    VirtualizingPanel.GetIsVirtualizing(firstGrid) && VirtualizingPanel.GetVirtualizationMode(firstGrid) == VirtualizationMode.Recycling &&
                    window.InputBindings.OfType<KeyBinding>().Any(binding => binding.Key == Key.F5), "shared grid style or refresh keyboard shortcut missing");
                model.SelectedProfessor = model.Professors.Single();
                model.SelectedDraft = model.Drafts.Single(item => item.Id == draft.Id);
                for (var page = 0; page < tabs.Items.Count; page++)
                {
                    tabs.SelectedIndex = page; Pump();
                    Check(model.SelectedPage == page, "page selection binding failed");
                    Snapshot(window, Path.Combine(output, $"page-{page + 1}.png"));
                    if (page == 3)
                    {
                        var detail = Descendants<TabControl>(window).Single(control => control != tabs);
                        for (var index = 0; index < detail.Items.Count; index++)
                        {
                            detail.SelectedIndex = index; Pump();
                            Snapshot(window, Path.Combine(output, $"detail-{index + 1}.png"));
                        }
                    }
                }
                Check(bindings.Messages.Count == 0, string.Join("\n", bindings.Messages));
            });

            Test("recommendation center keeps scope, coverage and run comparison visible", () =>
            {
                tabs.SelectedIndex = 8; Pump();
                Check(model.RecommendationCenter.Runs.Count == 2 && model.RecommendationCenter.Items.Count == 1,
                    "recommendation runs were not loaded into the center");
                model.RecommendationCenter.SelectedRun = model.RecommendationCenter.Runs.Single(run => run.Level == "Semantic");
                model.RecommendationCenter.CompareRun = model.RecommendationCenter.Runs.Single(run => run.Level == "Base"); Pump();
                Check(model.RecommendationCenter.ScopeSummary.Contains("候选快照") && model.RecommendationCenter.CoverageSummary.Contains("Eligible") &&
                    model.RecommendationCenter.Comparison.Count == 1 && model.RecommendationCenter.Comparison[0].ScoreChange == "+25",
                    "recommendation center did not preserve candidate scope, coverage groups and score changes");
                Snapshot(window, Path.Combine(output, "recommendation-center.png"));
            });

            Test("draft selection, editing, saving and refresh preserve the editor", () =>
            {
                tabs.SelectedIndex = 4; Pump();
                var list = Descendants<ListBox>(window).Single();
                list.SelectedItem = model.Drafts.Single(item => item.Id == draft.Id); Pump();
                var body = Descendants<TextBox>(window).Single(box => BindingOperations.GetBinding(box, TextBox.TextProperty)?.Path.Path == "Body");
                Check(!body.IsReadOnly, "draft should be editable");
                body.Text = "人工编辑后保存的正文"; Pump();
                model.SaveDraftCommand.Execute(null); Pump();
                Check(outreach.Get(draft.Id).Body == body.Text, "editor changes were not saved");
                body.Text = "尚未保存的正文"; Pump();
                model.RefreshCommand.Execute(null); Pump();
                Check(body.Text == "尚未保存的正文", "refresh discarded unsaved changes");
                list.SelectedItem = model.Drafts.Single(item => item.Id == sent.Id); Pump();
                Check(body.IsReadOnly && !model.SaveDraftCommand.CanExecute(null), "sent message must be locked");
                list.SelectedItem = model.Drafts.Single(item => item.Id == draft.Id); Pump();
                list.SelectedItem = null; Pump();
                Check(body.IsReadOnly && body.Text == "" && model.Recipient == "" && model.Subject == "", "empty selection retained an editable old message");
                Check(!model.SaveDraftCommand.CanExecute(null) && !model.SendCommand.CanExecute(null), "empty editor commands enabled");
            });

            Test("filtering away the selected professor clears dependent detail", () =>
            {
                model.SelectedProfessor = model.Professors.Single();
                Check(model.ProfessorHistory.Count == 2, "seed history missing");
                model.ProfessorFilter = "no such professor";
                model.RefreshCommand.Execute(null); Pump();
                Check(model.SelectedProfessor == null && model.ProfessorHistory.Count == 0 && model.Papers.Count == 0,
                    "old professor details survived empty selection");
                Check(!model.AnalyzeCommand.CanExecute(null), "analysis enabled without professor");
            });

            Test("component retry and UI-thread shutdown", () =>
            {
                var originalHost = model.Settings.AgentHostPath;
                model.Settings.AgentHostPath = Path.Combine(directory, "missing.js");
                model.ConnectAgentCommand.Execute(null);
                WaitFor(() => model.ConnectAgentCommand.CanExecute(null), 15);
                Check(!model.ComponentReady && model.ComponentStatus.Contains("失败"), "missing host did not fail clearly");
                model.Settings.AgentHostPath = originalHost;
                model.ConnectAgentCommand.Execute(null);
                WaitFor(() => model.ComponentReady, 100);
                Check(model.Providers.Single(provider => provider.Id == "deepseek").Configured, "component restart forgot the saved model account");
                Check(Task.Run(async () => await model.DisposeAsync()).Wait(TimeSpan.FromSeconds(10)), "connected application deadlocked during shutdown");
                model = null;
            });
            Test("no WPF binding errors during interactions", () => Check(bindings.Messages.Count == 0, string.Join("\n", bindings.Messages)));
            PresentationTraceSources.DataBindingSource.Listeners.Remove(bindings);
        }
        catch (Exception error) { failures++; Results.Add(new { name = "test setup", passed = false, error = error.ToString() }); Console.WriteLine(error); }
        finally
        {
            window?.Close();
            if (model != null)
            {
                var dispose = Task.Run(async () => await model.DisposeAsync());
                var timer = Stopwatch.StartNew();
                while (!dispose.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(15)) { Pump(); Thread.Sleep(15); }
            }
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, true); } catch (IOException) { }
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new
            {
                platform = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(),
                executedAt = DateTimeOffset.UtcNow, realAccountsUsed = false, realMailSent = false,
                failures, results = Results
            }, new JsonSerializerOptions { WriteIndented = true }));
            app.Shutdown();
        }
        return failures == 0 ? 0 : 1;
    }

    private static void Execute(LocalDatabase database, string sql)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, sql);
        command.ExecuteNonQuery();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void WaitFor(Func<bool> condition, int seconds)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(seconds)) { Pump(); Thread.Sleep(15); }
        Check(condition(), "operation timed out");
    }
    private static void Pump()
    {
        foreach (Window active in Application.Current.Windows) active.UpdateLayout();
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Snapshot(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrEmpty(message)) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
    private sealed class GoogleHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object value = request.RequestUri!.AbsolutePath == "/token"
                ? new { access_token = "synthetic-access", refresh_token = "synthetic-refresh", expires_in = 3600, scope = GmailAuth.Scopes }
                : new { emailAddress = "sender@example.org" };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") });
        }
    }
    private sealed class TestTransport : IMailTransport
    {
        public Task<SendReceipt> SendAsync(SendSnapshot snapshot, CancellationToken cancellationToken) => Task.FromResult(new SendReceipt("synthetic-message", "synthetic-thread"));
    }
}
