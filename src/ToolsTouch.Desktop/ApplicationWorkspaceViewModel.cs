using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using ToolsTouch.Application;

namespace ToolsTouch.Desktop;

public sealed class ApplicationWorkspaceViewModel : Observable
{
    private readonly IApplicationCaseService cases;
    private readonly Action<Exception> reportError;
    private readonly Func<string, bool> confirmStageChange;
    private ApplicationCaseSummary? selectedCase;
    private ApplicationMaterialRecord? selectedMaterial;
    private ApplicationReminderRecord? selectedReminder;
    private string status = "尚未加载申请记录。";
    private string stage = "Interested";
    private string priority = "0";
    private string applicationUrl = "";
    private string nextStep = "";
    private string nextStepDueAt = "";
    private string ownerNote = "";
    private string submissionDate = "";
    private string submissionReference = "";
    private string receiptArtifactId = "";
    private string submissionNote = "";
    private string materialName = "";
    private bool materialRequired = true;
    private string materialState = "Missing";
    private string materialNote = "";
    private string reminderKind = "FollowUp";
    private string reminderDueAt = "";
    private string reminderBasis = "";
    private string reminderState = "Pending";

    public ObservableCollection<ApplicationCaseSummary> Cases { get; } = [];
    public ObservableCollection<ApplicationMaterialRecord> Materials { get; } = [];
    public ObservableCollection<ApplicationEventRecord> Events { get; } = [];
    public ObservableCollection<ApplicationReminderRecord> Reminders { get; } = [];
    public IReadOnlyList<string> StageOptions => ApplicationStages.All;
    public IReadOnlyList<string> MaterialStateOptions { get; } = ["Missing", "Ready", "Submitted", "NotApplicable"];
    public IReadOnlyList<string> ReminderStateOptions { get; } = ["Pending", "Completed", "Dismissed"];

    public ApplicationCaseSummary? SelectedCase
    {
        get => selectedCase;
        set
        {
            if (!Set(ref selectedCase, value)) return;
            LoadDetail();
            Raise(nameof(HasSelectedCase));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public ApplicationMaterialRecord? SelectedMaterial
    {
        get => selectedMaterial;
        set
        {
            if (!Set(ref selectedMaterial, value)) return;
            if (value is not null)
            {
                MaterialRequired = value.IsRequired;
                MaterialState = value.State;
                MaterialNote = value.Note ?? "";
            }
            Raise(nameof(HasSelectedMaterial));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public ApplicationReminderRecord? SelectedReminder
    {
        get => selectedReminder;
        set
        {
            if (!Set(ref selectedReminder, value)) return;
            if (value is not null)
            {
                ReminderDueAt = value.DueAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                ReminderBasis = value.Basis;
                ReminderState = value.State;
            }
            Raise(nameof(HasSelectedReminder));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool HasSelectedCase => SelectedCase is not null;
    public bool HasSelectedMaterial => SelectedMaterial is not null;
    public bool HasSelectedReminder => SelectedReminder is not null;
    public string Status { get => status; private set => Set(ref status, value); }
    public string Stage { get => stage; set => Set(ref stage, value); }
    public string Priority { get => priority; set => Set(ref priority, value); }
    public string ApplicationUrl { get => applicationUrl; set => Set(ref applicationUrl, value); }
    public string NextStep { get => nextStep; set => Set(ref nextStep, value); }
    public string NextStepDueAt { get => nextStepDueAt; set => Set(ref nextStepDueAt, value); }
    public string OwnerNote { get => ownerNote; set => Set(ref ownerNote, value); }
    public string SubmissionDate { get => submissionDate; set => Set(ref submissionDate, value); }
    public string SubmissionReference { get => submissionReference; set => Set(ref submissionReference, value); }
    public string ReceiptArtifactId { get => receiptArtifactId; set => Set(ref receiptArtifactId, value); }
    public string SubmissionNote { get => submissionNote; set => Set(ref submissionNote, value); }
    public string MaterialName { get => materialName; set => Set(ref materialName, value); }
    public bool MaterialRequired { get => materialRequired; set => Set(ref materialRequired, value); }
    public string MaterialState { get => materialState; set => Set(ref materialState, value); }
    public string MaterialNote { get => materialNote; set => Set(ref materialNote, value); }
    public string ReminderKind { get => reminderKind; set => Set(ref reminderKind, value); }
    public string ReminderDueAt { get => reminderDueAt; set => Set(ref reminderDueAt, value); }
    public string ReminderBasis { get => reminderBasis; set => Set(ref reminderBasis, value); }
    public string ReminderState { get => reminderState; set => Set(ref reminderState, value); }

    public ICommand RefreshCommand { get; }
    public ICommand SaveDetailsCommand { get; }
    public ICommand ChangeStageCommand { get; }
    public ICommand RecordSubmissionCommand { get; }
    public ICommand AddMaterialCommand { get; }
    public ICommand UpdateMaterialCommand { get; }
    public ICommand AddReminderCommand { get; }
    public ICommand CompleteReminderCommand { get; }

    public ApplicationWorkspaceViewModel(IApplicationCaseService cases, Action<Exception> reportError, Func<string, bool> confirmStageChange)
    {
        this.cases = cases;
        this.reportError = reportError;
        this.confirmStageChange = confirmStageChange;
        RefreshCommand = new UiCommand(() => { Refresh(); return Task.CompletedTask; }, reportError);
        SaveDetailsCommand = new UiCommand(() => { SaveDetails(); return Task.CompletedTask; }, reportError, () => HasSelectedCase);
        ChangeStageCommand = new UiCommand(() => { ChangeStage(); return Task.CompletedTask; }, reportError, () => HasSelectedCase);
        RecordSubmissionCommand = new UiCommand(() => { RecordSubmission(); return Task.CompletedTask; }, reportError, () => HasSelectedCase);
        AddMaterialCommand = new UiCommand(() => { AddMaterial(); return Task.CompletedTask; }, reportError, () => HasSelectedCase && !string.IsNullOrWhiteSpace(MaterialName));
        UpdateMaterialCommand = new UiCommand(() => { UpdateMaterial(); return Task.CompletedTask; }, reportError, () => HasSelectedMaterial);
        AddReminderCommand = new UiCommand(() => { AddReminder(); return Task.CompletedTask; }, reportError, () => HasSelectedCase && !string.IsNullOrWhiteSpace(ReminderKind));
        CompleteReminderCommand = new UiCommand(() => { CompleteReminder(); return Task.CompletedTask; }, reportError, () => HasSelectedReminder);
    }

    public void Refresh()
    {
        var caseId = SelectedCase?.Case.Id;
        Replace(Cases, cases.List());
        SelectedCase = Cases.FirstOrDefault(item => item.Case.Id == caseId) ?? Cases.FirstOrDefault();
        if (SelectedCase is null) ClearDetail();
        Status = $"已加载 {Cases.Count} 条申请记录。阶段变更和官网提交均需用户明确记录。";
    }

    private void LoadDetail()
    {
        ClearDetail();
        if (SelectedCase is null) return;
        var record = cases.Get(SelectedCase.Case.Id);
        Stage = record.Stage;
        Priority = record.Priority.ToString(CultureInfo.InvariantCulture);
        ApplicationUrl = record.ApplicationUrl ?? "";
        NextStep = record.NextStep ?? "";
        NextStepDueAt = FormatDate(record.NextStepDueAt);
        OwnerNote = record.OwnerNote ?? "";
        SubmissionDate = FormatDate(record.SubmittedAt);
        SubmissionReference = record.SubmissionReference ?? "";
        ReceiptArtifactId = record.ReceiptArtifactId ?? "";
        Materials.AddRange(cases.Materials(record.Id));
        Events.AddRange(cases.Events(record.Id));
        Reminders.AddRange(cases.Reminders(record.Id));
        SelectedMaterial = Materials.FirstOrDefault();
        SelectedReminder = Reminders.FirstOrDefault();
    }

    private void ClearDetail()
    {
        Materials.Clear(); Events.Clear(); Reminders.Clear(); SelectedMaterial = null; SelectedReminder = null;
    }

    private void SaveDetails()
    {
        var record = SelectedCase?.Case ?? throw new InvalidOperationException("APPLICATION_CASE_NOT_SELECTED");
        if (!int.TryParse(Priority, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPriority)) throw new ArgumentException("优先级必须是 0 到 5 的整数。");
        var saved = cases.Update(new ApplicationCaseUpdateRequest(record.Id, record.Revision, parsedPriority, ApplicationUrl,
            NextStep, ParseOptionalDate(NextStepDueAt, "下一步时间"), OwnerNote));
        RefreshSelected(saved.Id);
        Status = "申请记录已保存。";
    }

    private void ChangeStage()
    {
        var record = SelectedCase?.Case ?? throw new InvalidOperationException("APPLICATION_CASE_NOT_SELECTED");
        var next = ApplicationStages.Validate(Stage);
        if (next == record.Stage) throw new InvalidOperationException("APPLICATION_STAGE_UNCHANGED");
        if (!confirmStageChange($"确认把申请“{record.Id}”从 {record.Stage} 改为 {next}？此操作会追加一条阶段事件。")) return;
        var saved = cases.ChangeStage(new ApplicationStageChangeRequest(record.Id, record.Revision, next, DateTimeOffset.UtcNow, "用户在申请工作台确认阶段变更"));
        RefreshSelected(saved.Id);
        Status = $"申请阶段已记录为 {saved.Stage}。";
    }

    private void RecordSubmission()
    {
        var record = SelectedCase?.Case ?? throw new InvalidOperationException("APPLICATION_CASE_NOT_SELECTED");
        var submittedAt = string.IsNullOrWhiteSpace(SubmissionDate) ? DateTimeOffset.UtcNow : ParseDate(SubmissionDate, "官网提交时间");
        var saved = cases.RecordSubmission(new ApplicationSubmissionRequest(record.Id, record.Revision, submittedAt,
            SubmissionReference, ReceiptArtifactId, SubmissionNote));
        RefreshSelected(saved.Id);
        Status = "官网提交回执已记录；申请阶段不会被邮件发送自动改写。";
    }

    private void AddMaterial()
    {
        var record = SelectedCase?.Case ?? throw new InvalidOperationException("APPLICATION_CASE_NOT_SELECTED");
        cases.AddMaterial(new ApplicationMaterialCreateRequest(record.Id, MaterialName, MaterialRequired, MaterialState, null, MaterialNote));
        MaterialName = ""; MaterialNote = ""; RefreshSelected(record.Id);
        Status = "材料清单已添加。";
    }

    private void UpdateMaterial()
    {
        var material = SelectedMaterial ?? throw new InvalidOperationException("APPLICATION_MATERIAL_NOT_SELECTED");
        cases.UpdateMaterial(new ApplicationMaterialUpdateRequest(material.Id, material.Revision, MaterialRequired, MaterialState, material.ArtifactId, MaterialNote));
        RefreshSelected(material.CaseId);
        Status = "材料状态已保存。";
    }

    private void AddReminder()
    {
        var record = SelectedCase?.Case ?? throw new InvalidOperationException("APPLICATION_CASE_NOT_SELECTED");
        var due = ParseDate(ReminderDueAt, "提醒时间");
        cases.AddReminder(new ApplicationReminderCreateRequest(record.Id, null, ReminderKind, due, ReminderBasis, ReminderState));
        ReminderKind = "FollowUp"; ReminderDueAt = ""; ReminderBasis = ""; RefreshSelected(record.Id);
        Status = "提醒已添加。";
    }

    private void CompleteReminder()
    {
        var reminder = SelectedReminder ?? throw new InvalidOperationException("APPLICATION_REMINDER_NOT_SELECTED");
        cases.UpdateReminder(new ApplicationReminderUpdateRequest(reminder.Id, "Completed", reminder.DueAt, reminder.Basis));
        RefreshSelected(reminder.CaseId!);
        Status = "提醒已标记完成。";
    }

    private void RefreshSelected(string id)
    {
        var selected = id;
        Replace(Cases, cases.List());
        SelectedCase = Cases.FirstOrDefault(item => item.Case.Id == selected);
    }

    private static DateTimeOffset ParseDate(string value, string label)
    {
        if (DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var parsed)) return parsed;
        throw new ArgumentException($"{label}格式无效，请输入可识别的日期时间。");
    }

    private static DateTimeOffset? ParseOptionalDate(string value, string label) => string.IsNullOrWhiteSpace(value) ? null : ParseDate(value, label);
    private static string FormatDate(DateTimeOffset? value) => value is null ? "" : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }
}

file static class CollectionExtensions
{
    public static void AddRange<T>(this ObservableCollection<T> target, IEnumerable<T> values)
    {
        foreach (var value in values) target.Add(value);
    }
}
