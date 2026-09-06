using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using ToolsTouch.Application;
using ToolsTouch.Core;

namespace ToolsTouch.Desktop;

public sealed partial class MainViewModel : Observable, IAsyncDisposable
{
    private readonly AppServices services;
    private readonly LocalDatabase database;
    private readonly ResearchStore store;
    private readonly LibraryService library;
    private readonly OutreachService outreach;
    private readonly SendCoordinator sendCoordinator;
    private readonly IGmailAccountPort gmailAuth;
    private readonly GmailService gmail;
    private readonly PublicWeb web;
    private CancellationTokenSource? gmailCancellation;
    private bool mailBusy;
    public bool MailBusy
    {
        get => mailBusy;
        private set
        {
            if (!Set(ref mailBusy, value)) return;
            Raise(nameof(DraftLocked)); Raise(nameof(DraftEditorStatus));
            CommandManager.InvalidateRequerySuggested();
        }
    }
    private readonly HttpClient http;
    private AgentBridge? bridge;
    private DiscoveryService? discovery;
    private CancellationTokenSource? taskCancellation;
    private string? profileId;
    private bool refreshing;
    private bool disposed;
    public DesktopSettings Settings { get; }
    public string DataDirectory => Settings.StorageDirectory;
    public string GmailStatus => gmailAuth.Account == null ? "Gmail 未连接" : "Gmail · " + gmailAuth.Account;
    public ObservableCollection<Professor> Professors { get; } = [];
    public ObservableCollection<Paper> Papers { get; } = [];
    public ObservableCollection<OutreachHistory> ProfessorHistory { get; } = [];
    public ObservableCollection<Draft> Drafts { get; } = [];
    public ObservableCollection<AgentRun> Runs { get; } = [];
    public ObservableCollection<string> Models { get; } = [];
    public ObservableCollection<string> Activity { get; } = [];
    public AdmissionWorkspaceViewModel AdmissionWorkspace { get; }
    public FacultyWorkspaceViewModel FacultyWorkspace { get; }
    public RecommendationCenterViewModel RecommendationCenter { get; }
    public ApplicationWorkspaceViewModel ApplicationWorkspace { get; }
    public RecordGridViewModel RecordGrid { get; }
    private string status = "组件加载中…";
    public string Status { get => status; private set => Set(ref status, value); }
    public string Query { get; set; } = "帮我找做 World Model 的老师";
    public string ProfessorFilter { get; set; } = "";
    private int selectedPage;
    public int SelectedPage
    {
        get => selectedPage;
        set
        {
            if (!Enum.IsDefined(typeof(WorkspacePage), value) || selectedPage == value) return;
            if (value == (int)WorkspacePage.ProfessorDetail && selectedPage is 1 or 2) professorReturnPage = selectedPage;
            Set(ref selectedPage, value);
            Raise(nameof(SelectedNavigationPage)); Raise(nameof(PageTitle)); Raise(nameof(PageDescription));
        }
    }
    private bool busy;
    public bool Busy { get => busy; private set { Set(ref busy, value); Raise(nameof(AccountControlsEnabled)); CommandManager.InvalidateRequerySuggested(); } }
    private Professor? selectedProfessor;
    public Professor? SelectedProfessor
    {
        get => selectedProfessor;
        set
        {
            if (!Set(ref selectedProfessor, value) || refreshing) return;
            SelectedPaper = null; SelectedHistory = null;
            PaperText = "选择论文后点击阅读。";
            LoadProfessor();
        }
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
            if (Set(ref selectedDraft, value) && !refreshing) LoadDraftEditor();
        }
    }
    private void LoadDraftEditor()
    {
        Recipient = SelectedDraft?.Recipient ?? ""; Subject = SelectedDraft?.Subject ?? "";
        Body = SelectedDraft?.Body ?? ""; Attachment = SelectedDraft?.CvPath ?? "";
        Raise(nameof(Recipient)); Raise(nameof(Subject)); Raise(nameof(Body)); Raise(nameof(Attachment));
        Raise(nameof(DraftLocked)); Raise(nameof(DraftEditorStatus));
        CommandManager.InvalidateRequerySuggested();
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
    public string Summary => $"导师 {Professors.Count} · 待编辑 {Drafts.Count(d => d.State == "Draft")} · 已发送 {Drafts.Count(d => d.State == "Sent")} · 已回复 {outreach.ReplyCount()} · 待处理 {Runs.Count(r => r.State is "Queued" or "Partial" or "Failed" or "Cancelled")}";
    public ICommand RefreshCommand { get; }
    public ICommand ConnectAgentCommand { get; private set; } = null!;
    public ICommand LoginCommand { get; private set; } = null!;
    public ICommand OpenLoginCommand { get; private set; } = null!;
    public ICommand AuthReplyCommand { get; private set; } = null!;
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
    public ICommand ConnectGmailCommand { get; private set; } = null!;
    public ICommand SendCommand { get; }
    public ICommand ReconcileCommand { get; }
    public ICommand SyncRepliesCommand { get; }

    public MainViewModel(string? dataDirectory = null, HttpClient? accountHttp = null, Action<Uri>? openAccountBrowser = null, Action<string>? copyAccountUrl = null)
    {
        http = accountHttp ?? new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        if (openAccountBrowser != null) loginBrowser = openAccountBrowser;
        if (copyAccountUrl != null) copyLoginUrl = copyAccountUrl;
        Settings = DesktopSettings.Load(dataDirectory);
        Directory.CreateDirectory(DataDirectory);
        services = new AppServices(DataDirectory, http, () => GoogleClient.FromFile(Settings.GoogleClientFile), Settings.PythonPath);
        diagnostics = services.Diagnostics;
        database = services.Database;
        store = services.Store;
        library = services.Library;
        outreach = services.Outreach;
        sendCoordinator = services.SendCoordinator;
        gmailAuth = services.GmailAuth;
        gmail = services.Gmail;
        web = services.Web;
        AdmissionWorkspace = new AdmissionWorkspaceViewModel(services.AdmissionQueries, ReportError);
        FacultyWorkspace = new FacultyWorkspaceViewModel(services.FacultyQueries, ReportError);
        RecommendationCenter = new RecommendationCenterViewModel(services.Recommendations, ReportError);
        ApplicationWorkspace = new ApplicationWorkspaceViewModel(services.ApplicationCases, ReportError,
            message => MessageBox.Show(message, "确认申请阶段变更", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes);
        RecordGrid = new RecordGridViewModel(services.SystemCollections, services.RecordWorkspace, services.FieldSchemas,
            services.RecordQueries, services.Views, services.Imports, ReportError);
        ConfigureAccounts();
        store.RecoverInterruptedRuns(); outreach.RecoverInterruptedSends(); sendCoordinator.RecoverInterruptedSends();
        UiCommand Command(Func<Task> action, Func<bool>? enabled = null) => new(action, ReportError, () => IsSignedIn && (enabled?.Invoke() ?? true));
        RefreshCommand = Command(() => { Refresh(); return Task.CompletedTask; });
        DiscoverCommand = Command(() => StartTaskAsync("Discover"), () => CanUseModel && !string.IsNullOrWhiteSpace(Query));
        AnalyzeCommand = Command(() => StartTaskAsync("Analyze"), () => CanUseModel && SelectedProfessor != null);
        GenerateDraftCommand = Command(() => StartTaskAsync("Draft"), () => CanUseModel && SelectedProfessor != null);
        ResumeCommand = Command(async () => { await EnsureAgentAsync(); await ExecuteTaskAsync(SelectedRun!.Id); }, () => CanUseModel && SelectedRun?.State is "Queued" or "Partial" or "Failed" or "Cancelled");
        CancelCommand = Command(CancelCurrentAsync, () => Busy || AuthBusy || mailBusy);
        ViewProfessorCommand = Command(() => { LoadProfessor(); SelectedPage = (int)WorkspacePage.ProfessorDetail; return Task.CompletedTask; }, () => SelectedProfessor != null);
        OpenHomepageCommand = Command(() => { if (SelectedProfessor?.Homepage is { } homepage) OpenUrl(homepage); return Task.CompletedTask; }, () => SelectedProfessor?.Homepage != null);
        ReadPaperCommand = Command(async () =>
        {
            var paper = SelectedPaper!;
            PaperText = "正在获取论文…";
            var startPage = PaperStartPage;
            var reading = await Task.Run(() => library.ReadPaperAsync(paper.Id, startPage, 10, CancellationToken.None));
            PaperText = $"{paper.Title}\n{reading.Coverage}\n{reading.SourceUrl}\n{reading.Note}\n\n" + string.Join("\n\n", reading.Pages.Select(page => $"页 {page.Page}\n{page.Text}"));
        }, () => SelectedPaper != null);
        OpenPaperSourceCommand = Command(() => { OpenUrl(SelectedPaper!.SourceUrl); return Task.CompletedTask; }, () => SelectedPaper != null);
        OpenHistoryDraftCommand = Command(() => { SelectedDraft = outreach.Get(SelectedHistory!.Id); SelectedPage = (int)WorkspacePage.Outreach; return Task.CompletedTask; }, () => SelectedHistory != null);
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
        SaveSettingsCommand = Command(() => { Settings.Save(); Status = "设置已保存。"; return Task.CompletedTask; }, () => !Busy);
        SendCommand = Command(SendAsync, () => CanEditDraft() && gmail.Account != null && !mailBusy);
        ReconcileCommand = Command(async () =>
        {
            MailBusy = true;
            try
            {
                var id = SelectedDraft!.Id;
                Status = await outreach.ReconcileAsync(id, gmail) ? "已在 Gmail 已发送邮件中核实。" : "尚未找到对应邮件，保留结果不确定状态，不会重发。";
                SelectedDraft = outreach.Get(id); Refresh();
            }
            finally { MailBusy = false; }
        }, () => SelectedDraft?.State == "Unknown" && gmail.Account != null && !mailBusy);
        SyncRepliesCommand = Command(async () =>
        {
            MailBusy = true;
            try { var count = await outreach.SyncRepliesAsync(gmail); Refresh(); Status = $"同步完成，{count} 个会话有回复。"; }
            finally { MailBusy = false; }
        }, () => gmail.Account != null && !mailBusy);
        Refresh();
        var current = library.GetProfile();
        if (current != null) LoadProfile(current);
    }

    private bool CanEditDraft() => !mailBusy && SelectedDraft?.State is "Draft" or "Failed";
    private static string NewId() => Guid.NewGuid().ToString("N");
    private static void OpenUrl(string url)
    {
        var uri = PublicWeb.ValidateUri(url);
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private void OnUi(Action action)
    {
        if (!disposed && !System.Windows.Application.Current.Dispatcher.HasShutdownStarted) System.Windows.Application.Current.Dispatcher.BeginInvoke(action);
    }
    private async Task StartTaskAsync(string kind)
    {
        await EnsureAgentAsync();
        var run = discovery!.Create(kind, Query, kind == "Discover" ? null : SelectedProfessor!.Id, Settings.MaxToolCalls,
            checked(Settings.StageTimeoutSeconds * 1000), Settings.Provider, Settings.Model);
        await ExecuteTaskAsync(run.Id);
        if (kind == "Draft") SelectedPage = (int)WorkspacePage.Outreach;
    }
    private async Task ExecuteTaskAsync(string runId)
    {
        Busy = true;
        taskCancellation = new();
        try { await discovery!.ExecuteAsync(runId, Settings.Model, taskCancellation.Token, Settings.Provider); Status = "任务已完成。"; }
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
            AdmissionWorkspace.Refresh();
            FacultyWorkspace.Refresh();
            RecommendationCenter.Refresh();
            ApplicationWorkspace.Refresh();
            RecordGrid.Refresh();
            Replace(Professors, store.SearchProfessors(ProfessorFilter));
            // Keep an editor's loaded revision until explicit save/reload; stale saves fail optimistically.
            Replace(Drafts, outreach.List().Select(item => item.Id == draft?.Id ? draft : item)); Replace(Runs, store.ListRuns());
            SelectedProfessor = Professors.FirstOrDefault(item => item.Id == professorId);
            SelectedDraft = Drafts.FirstOrDefault(item => item.Id == draft?.Id);
            SelectedRun = Runs.FirstOrDefault(item => item.Id == runId);
            Raise(nameof(Summary));
        }
        finally { refreshing = false; }
        if (SelectedDraft == null) LoadDraftEditor();
        LoadProfessor();
    }
    private void LoadProfessor()
    {
        if (SelectedProfessor == null)
        {
            Papers.Clear(); ProfessorHistory.Clear(); SelectedPaper = null; SelectedHistory = null;
            Analysis = "选择导师后查看研究分析。"; PaperText = "选择论文后点击阅读。";
            return;
        }
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
        MailBusy = true;
        try
        {
            SaveDraft();
            var reviewed = SelectedDraft!;
            var preview = sendCoordinator.Prepare(reviewed.Id, gmail.Account!);
            if (MessageBox.Show($"从：{preview.SenderAccount}\n收件人：{preview.Recipient}\n主题：{preview.Subject}\n附件：{(preview.AttachmentName == null ? "无附件" : $"{preview.AttachmentName}（{preview.AttachmentBytes} bytes）")}\n\n{preview.Body}\n\n确认发送这份不可变邮件快照？\n确认有效至：{preview.ExpiresAt:HH:mm:ss}", "确认发送邮件", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            try
            {
                var attempt = sendCoordinator.Confirm(preview.ConfirmationId, gmail.Account!);
                await sendCoordinator.SendAsync(attempt.Id, gmail);
                Status = "Gmail 已接受发送请求。";
            }
            finally { SelectedDraft = outreach.Get(reviewed.Id); Refresh(); }
        }
        finally { MailBusy = false; }
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
        disposed = true; componentCancellation.Cancel(); taskCancellation?.Cancel(); gmailCancellation?.Cancel();
        if (bridge != null) await bridge.DisposeAsync().ConfigureAwait(false);
        services.Dispose();
    }
}
