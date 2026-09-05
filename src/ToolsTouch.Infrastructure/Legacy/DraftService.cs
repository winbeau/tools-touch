using System.Text.Json;
using ToolsTouch.Application;
using ToolsTouch.Core;

namespace ToolsTouch.Infrastructure.Legacy;

public sealed class DraftService(OutreachService outreach) : IDraftService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public DraftResult Create(DraftRequest request)
    {
        DraftRequestContract.Validate(request);
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        var existing = outreach.FindByIdempotencyKey(request.IdempotencyKey);
        if (existing is not null)
        {
            // A resumed model attempt must return the saved draft, including human edits.
            // Compare the immutable request envelope rather than the mutable draft body.
            if (existing.DraftRequestJson == "{}" || existing.DraftRequestJson != requestJson)
                throw new InvalidOperationException("IDEMPOTENCY_CONFLICT");
            return new(existing, true);
        }
        var draft = outreach.CreateDraft(request.ProfessorId, request.Recipient, request.Subject, request.Body, request.CvPath,
            request.IdempotencyKey, request.EvidenceJson, request.ProfileId, request.AppointmentId, request.PreferenceId,
            request.AnalysisArtifactId, request.Language, request.PromptVersion, request.Model,
            requestJson);
        return new(draft, false);
    }

    public IReadOnlyList<DraftChangeRecord> ListChanges(string draftId) => outreach.ListChanges(draftId).Select(change =>
        new DraftChangeRecord(change.Id, change.DraftId, change.Revision, change.ChangeKind, change.SnapshotJson, change.CreatedAt)).ToArray();
}
