using ToolsTouch.Application;
using ToolsTouch.Core;
using ToolsTouch.Infrastructure.Tracking;

static class ApplicationCaseTests
{
    public static async Task RunAsync(LocalDatabase database)
    {
        var service = new ApplicationCaseService(database);
        var casesBefore = service.List().Count;
        var created = service.Create(new ApplicationCaseCreateRequest(
            "department-a", 2026, "Master", "program-a", "round-2026", "professor-shared", "Preparing", 3,
            "https://apply.example.org/2026", "整理推荐信", DateTimeOffset.UtcNow.AddDays(3), "重点申请", "application-case-1"));
        Check(created.Id == "application-case-1" && created.Stage == "Preparing" && created.Revision == 1 && created.Priority == 3,
            "application case creation persists identity and editable tracking fields");
        Check(service.List().Count == casesBefore + 1 && service.List().Single(item => item.Case.Id == created.Id).SchoolName == "National University",
            "application list joins scoped school and department names");
        Check(service.Events(created.Id).Single(item => item.Type == "Created" && item.NextStage == "Preparing") is not null,
            "case creation appends an initial event");

        var updated = service.Update(new ApplicationCaseUpdateRequest(created.Id, created.Revision, 5, created.ApplicationUrl,
            "确认材料", DateTimeOffset.UtcNow.AddDays(5), "用户备注"));
        await ThrowsCode(() => Task.FromResult(service.Update(new ApplicationCaseUpdateRequest(created.Id, created.Revision, 1, null, null, null, null))), "APPLICATION_CASE_CHANGED");
        Check(updated.Priority == 5 && updated.NextStep == "确认材料" && updated.OwnerNote == "用户备注" && updated.Revision == 2,
            "case updates use a revision compare-and-swap");

        var changed = service.ChangeStage(new ApplicationStageChangeRequest(created.Id, updated.Revision, "Submitted",
            DateTimeOffset.Parse("2026-06-01T08:00:00Z"), "用户已在官网确认提交"));
        Check(changed.Stage == "Submitted" && changed.Revision == 3, "stage change is explicit and revisioned");
        var submitted = service.RecordSubmission(new ApplicationSubmissionRequest(created.Id, changed.Revision,
            DateTimeOffset.Parse("2026-06-01T08:00:00Z"), "receipt-2026-001", null, "官网回执编号"));
        Check(submitted.Stage == "Submitted" && submitted.SubmissionReference == "receipt-2026-001" && submitted.Revision == 4,
            "submission receipt records the website action without a hidden stage transition");
        Check(service.Events(created.Id).Count(item => item.Type == "StageChanged") == 1 &&
            service.Events(created.Id).Any(item => item.Type == "SubmissionRecorded" && item.Note == "官网回执编号"),
            "stage and submission history remain append-only");

        var material = service.AddMaterial(new ApplicationMaterialCreateRequest(created.Id, "CV", true, "Ready", null, "已确认版本", "material-1"));
        var materialUpdated = service.UpdateMaterial(new ApplicationMaterialUpdateRequest(material.Id, material.Revision, true, "Submitted", null, "随官网提交"));
        await ThrowsCode(() => Task.FromResult(service.UpdateMaterial(new ApplicationMaterialUpdateRequest(material.Id, material.Revision, true, "Missing", null, null))), "APPLICATION_MATERIAL_CHANGED");
        Check(materialUpdated.State == "Submitted" && service.Materials(created.Id).Single().Revision == 2,
            "material checklist has independent revisions and states");

        var reminder = service.AddReminder(new ApplicationReminderCreateRequest(created.Id, null, "FollowUp", DateTimeOffset.Parse("2026-06-08T08:00:00Z"), "官网提交后检查材料", Id: "reminder-1"));
        var reminderUpdated = service.UpdateReminder(new ApplicationReminderUpdateRequest(reminder.Id, "Completed", reminder.DueAt, "已完成官网回执核对"));
        Check(reminderUpdated.State == "Completed" && service.Reminders(created.Id).Single().State == "Completed",
            "case reminders persist due time, basis and completion state");

        var outreach = new OutreachService(database);
        var draft = outreach.CreateDraft("professor-shared", "contact@example.org", "Application contact", "Hello", null, "application-case-outreach");
        service.LinkOutreach(created.Id, draft.Id);
        Check(service.OutreachIds(created.Id).Single() == draft.Id && service.Get(created.Id).Stage == "Submitted",
            "outreach relation does not mutate the website application stage");
        var sent = await outreach.SendAsync(draft.Id, new FakeTransport());
        Check(sent.MessageId == "message" && service.Get(created.Id).Stage == "Submitted",
            "a sent mentor email never auto-marks the application as Submitted");
        Check(service.Events(created.Id).Any(item => item.Type == "OutreachLinked" && item.RelatedOutreachId == draft.Id),
            "outreach relation is auditable");

        await ThrowsCode(() => Task.FromResult(service.Create(new ApplicationCaseCreateRequest(
            "department-b", 2026, "Master", "program-a"))), "APPLICATION_PROGRAM_SCOPE_MISMATCH");
        await Throws<ArgumentException>(() => Task.FromResult(service.AddReminder(new ApplicationReminderCreateRequest(null, null, "Due", DateTimeOffset.UtcNow, "missing link"))));
        Console.WriteLine("PASS: application case identity, explicit stage events, website receipt, materials, reminders, outreach relation and sent-stage separation");
    }

    private static async Task ThrowsCode(Func<Task> action, string code)
    {
        try { await action(); }
        catch (InvalidOperationException error) when (error.Message == code) { return; }
        throw new Exception("Expected " + code);
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class FakeTransport : IMailTransport
    {
        public Task<SendReceipt> SendAsync(SendSnapshot snapshot, CancellationToken cancellationToken) => Task.FromResult(new SendReceipt("message", "thread"));
    }
}
