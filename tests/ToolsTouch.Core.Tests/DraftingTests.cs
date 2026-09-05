using System.Text.Json;
using ToolsTouch.Application;
using ToolsTouch.Core;
using ToolsTouch.Infrastructure.Legacy;

static class DraftingTests
{
    public static Task RunAsync(LocalDatabase database)
    {
        var outreach = new OutreachService(database);
        var drafts = new DraftService(outreach);
        var evidenceJson = "[{\"url\":\"https://example.org/research\",\"claim\":\"The source supports this draft\"}]";
        var request = new DraftRequest("prof", null, null, null, null, "en-US", "Write a concise research email",
            "recipient@example.org", "Generated subject", "Generated body", null, "model-v1", "prompt-v1", evidenceJson, "draft-task-1");

        var created = drafts.Create(request);
        Check(!created.Existing && created.Draft.Language == "en-US" && created.Draft.Model == "model-v1" &&
            created.Draft.PromptVersion == "prompt-v1" && created.Draft.EvidenceJson == evidenceJson &&
            JsonDocument.Parse(created.Draft.DraftRequestJson).RootElement.GetProperty("idempotencyKey").GetString() == "draft-task-1",
            "draft request metadata and immutable input are persisted");

        var retry = drafts.Create(request);
        Check(retry.Existing && retry.Draft.Id == created.Draft.Id && retry.Draft.Version == created.Draft.Version &&
            retry.Draft.Revision == 1, "same draft request key resumes the same version");

        var edited = outreach.Edit(created.Draft.Id, 1, request.Recipient, request.Subject, "Human edited body", null);
        var resumedAfterEdit = drafts.Create(request);
        Check(resumedAfterEdit.Existing && resumedAfterEdit.Draft.Body == "Human edited body" && resumedAfterEdit.Draft.Revision == 2,
            "resuming a request never overwrites a human edit");
        var changes = drafts.ListChanges(created.Draft.Id);
        Check(changes.Count == 2 && changes[0].ChangeKind == "Generated" && changes[0].Revision == 1 &&
            changes[1].ChangeKind == "Edited" && changes[1].Revision == 2 && changes[1].SnapshotJson.Contains("Human edited body"),
            "generated and edited draft snapshots are retained");
        Throws<InvalidOperationException>(() => drafts.Create(request with { Body = "Different body" }));
        Throws<InvalidOperationException>(() => outreach.Edit(created.Draft.Id, 1, request.Recipient, request.Subject, "Stale edit", null));

        var regeneratedRequest = request with { IdempotencyKey = "draft-task-2", Body = "Second generated body", PromptVersion = "prompt-v2" };
        var regenerated = drafts.Create(regeneratedRequest);
        Check(!regenerated.Existing && regenerated.Draft.Version == created.Draft.Version + 1 &&
            regenerated.Draft.Body == regeneratedRequest.Body && regenerated.Draft.PromptVersion == "prompt-v2" &&
            outreach.Get(created.Draft.Id).Body == "Human edited body",
            "a new request key creates a new version and preserves the previous draft");

        Throws<InvalidOperationException>(() => drafts.Create(request with { IdempotencyKey = "invalid-evidence", EvidenceJson = "{}" }));
        Throws<InvalidOperationException>(() => drafts.Create(request with { IdempotencyKey = "invalid-subject", Subject = "" }));
        var legacy = outreach.CreateDraft("prof", "legacy@example.org", "Legacy", "Legacy body", null, "legacy-draft-service");
        Check(legacy.Language == "zh-CN" && legacy.PromptVersion == "legacy" && legacy.EvidenceJson == "[]" &&
            outreach.ListChanges(legacy.Id).Single().ChangeKind == "Generated",
            "legacy OutreachService draft creation remains compatible and tracked");

        Console.WriteLine("PASS: draft request validation, metadata freeze, idempotent resume, human edit preservation, CAS, regeneration and legacy compatibility");
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
