using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using ToolsTouch.Core;
using ToolsTouch.Desktop;

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
            model = new MainViewModel(directory);
            window = new MainWindow { DataContext = model, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000 };
            using var bindings = new BindingErrors();
            PresentationTraceSources.DataBindingSource.Listeners.Add(bindings);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            window.Show(); Pump();
            var tabs = Descendants<TabControl>(window).First();

            Test("six pages and detail tabs render without binding errors", () =>
            {
                Check(tabs.Items.Count == 6, "expected six pages");
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

            Test("native Windows Pi handshake and UI-thread shutdown", () =>
            {
                model.ConnectAgentCommand.Execute(null);
                var timer = Stopwatch.StartNew();
                while (!model.ConnectAgentCommand.CanExecute(null) && timer.Elapsed < TimeSpan.FromSeconds(40))
                { Pump(); Thread.Sleep(15); }
                Check(model.Status == "Pi 已启动。" && model.Models.Count > 0, "Pi handshake failed: " + model.Status);
                // Matches App.OnExit: the UI thread waits while disposal runs on a worker.
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

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
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
    private sealed class TestTransport : IMailTransport
    {
        public Task<SendReceipt> SendAsync(SendSnapshot snapshot, CancellationToken cancellationToken) => Task.FromResult(new SendReceipt("synthetic-message", "synthetic-thread"));
    }
}
