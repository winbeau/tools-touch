using System.Text.Json;
using ToolsTouch.Core;

namespace ToolsTouch.Application;

public sealed record DraftRequest(
    string ProfessorId,
    string? AppointmentId,
    string? ProfileId,
    string? PreferenceId,
    string? AnalysisArtifactId,
    string Language,
    string WritingRequest,
    string Recipient,
    string Subject,
    string Body,
    string? CvPath,
    string Model,
    string PromptVersion,
    string EvidenceJson,
    string IdempotencyKey);

public sealed record DraftResult(Draft Draft, bool Existing);

public sealed record DraftChangeRecord(string Id, string DraftId, int Revision, string ChangeKind, string SnapshotJson, string CreatedAt);

public interface IDraftService
{
    DraftResult Create(DraftRequest request);
    IReadOnlyList<DraftChangeRecord> ListChanges(string draftId);
}

public static class DraftRequestContract
{
    public static void Validate(DraftRequest request)
    {
        Required(request.ProfessorId, "PROFESSOR_ID", 128);
        Optional(request.AppointmentId, "APPOINTMENT_ID", 128);
        Optional(request.ProfileId, "PROFILE_ID", 128);
        Optional(request.PreferenceId, "PREFERENCE_ID", 128);
        Optional(request.AnalysisArtifactId, "ANALYSIS_ARTIFACT_ID", 128);
        Required(request.Language, "LANGUAGE", 32);
        Required(request.WritingRequest, "WRITING_REQUEST", 20000);
        if (request.Recipient is null || request.Recipient.Length > 254) throw Invalid("RECIPIENT");
        Required(request.Subject, "SUBJECT", 300);
        Required(request.Body, "BODY", 100000);
        Required(request.Model, "MODEL", 128);
        Required(request.PromptVersion, "PROMPT_VERSION", 128);
        Required(request.EvidenceJson, "EVIDENCE_JSON", 200000);
        Required(request.IdempotencyKey, "IDEMPOTENCY_KEY", 512);
        try
        {
            using var evidence = JsonDocument.Parse(request.EvidenceJson);
            if (evidence.RootElement.ValueKind != JsonValueKind.Array) throw Invalid("EVIDENCE_JSON");
        }
        catch (JsonException) { throw Invalid("EVIDENCE_JSON"); }
    }

    private static void Required(string value, string code, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength) throw Invalid(code);
    }

    private static void Optional(string? value, string code, int maxLength)
    {
        if (value is not null && (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)) throw Invalid(code);
    }

    private static InvalidOperationException Invalid(string code) => new("DRAFT_REQUEST_INVALID:" + code);
}
