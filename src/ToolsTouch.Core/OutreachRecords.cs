namespace ToolsTouch.Core;

public sealed record Draft(string Id, string ProfessorId, int Version, string Recipient,
    string Subject, string Body, string? CvPath, string? CvHash, string State, int Revision = 1,
    string? AppointmentId = null, string? PreferenceId = null, string? AnalysisArtifactId = null,
    string Language = "zh-CN", string PromptVersion = "legacy", string? Model = null,
    string DraftRequestJson = "{}", string EvidenceJson = "[]", string? ProfileId = null);

public sealed record DraftChange(string Id, string DraftId, int Revision, string ChangeKind, string SnapshotJson, string CreatedAt);
