using ToolsTouch.Core;

namespace ToolsTouch.Application;

public static class ApplicationStages
{
    public static readonly IReadOnlyList<string> All =
        ["Interested", "Preparing", "Submitted", "Interview", "Waitlisted", "Offer", "Rejected", "Withdrawn", "Archived"];

    public static string Validate(string value)
    {
        value = Required(value, nameof(value));
        return All.Contains(value, StringComparer.Ordinal) ? value : throw new ArgumentException("Unknown application stage.", nameof(value));
    }

    public static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A value is required.", name) : value.Trim();

    public static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string? HttpUrl(string? value, string name)
    {
        value = Optional(value);
        if (value is not null && (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            throw new ArgumentException("Only an HTTP(S) URL is allowed.", name);
        return value;
    }

    public static DateTimeOffset? Date(DateTimeOffset? value) => value?.ToUniversalTime();
}

public sealed record ApplicationCaseRecord(
    string Id,
    string DepartmentId,
    string? ProgramId,
    string? RoundId,
    string? ProfessorId,
    int CycleYear,
    string DegreeType,
    string Stage,
    int Priority,
    string? ApplicationUrl,
    DateTimeOffset? SubmittedAt,
    string? SubmissionReference,
    string? ReceiptArtifactId,
    string? NextStep,
    DateTimeOffset? NextStepDueAt,
    string? OwnerNote,
    int Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ApplicationCaseSummary(
    ApplicationCaseRecord Case,
    string SchoolName,
    string DepartmentName,
    string? ProgramName,
    string? RoundTitle,
    string? ProfessorName,
    string ContactState,
    string ReplyState);

public sealed record ApplicationCaseCreateRequest(
    string DepartmentId,
    int CycleYear,
    string DegreeType,
    string? ProgramId = null,
    string? RoundId = null,
    string? ProfessorId = null,
    string Stage = "Interested",
    int Priority = 0,
    string? ApplicationUrl = null,
    string? NextStep = null,
    DateTimeOffset? NextStepDueAt = null,
    string? OwnerNote = null,
    string? Id = null);

public sealed record ApplicationCaseUpdateRequest(
    string CaseId,
    int ExpectedRevision,
    int Priority,
    string? ApplicationUrl,
    string? NextStep,
    DateTimeOffset? NextStepDueAt,
    string? OwnerNote);

public sealed record ApplicationStageChangeRequest(
    string CaseId,
    int ExpectedRevision,
    string NextStage,
    DateTimeOffset OccurredAt,
    string? Note = null,
    string? EvidenceArtifactId = null);

public sealed record ApplicationSubmissionRequest(
    string CaseId,
    int ExpectedRevision,
    DateTimeOffset SubmittedAt,
    string? SubmissionReference,
    string? ReceiptArtifactId,
    string? Note = null);

public sealed record ApplicationMaterialRecord(
    string Id,
    string CaseId,
    string Name,
    bool IsRequired,
    string State,
    string? ArtifactId,
    string? Note,
    int Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ApplicationMaterialCreateRequest(
    string CaseId,
    string Name,
    bool IsRequired = true,
    string State = "Missing",
    string? ArtifactId = null,
    string? Note = null,
    string? Id = null);

public sealed record ApplicationMaterialUpdateRequest(
    string MaterialId,
    int ExpectedRevision,
    bool IsRequired,
    string State,
    string? ArtifactId,
    string? Note);

public sealed record ApplicationReminderRecord(
    string Id,
    string? CaseId,
    string? RoundId,
    string Kind,
    DateTimeOffset DueAt,
    string Basis,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ApplicationReminderCreateRequest(
    string? CaseId,
    string? RoundId,
    string Kind,
    DateTimeOffset DueAt,
    string Basis,
    string State = "Pending",
    string? Id = null);

public sealed record ApplicationReminderUpdateRequest(
    string ReminderId,
    string State,
    DateTimeOffset DueAt,
    string Basis);

public sealed record ApplicationEventRecord(
    string Id,
    string CaseId,
    string Type,
    string? PreviousStage,
    string? NextStage,
    string? RelatedOutreachId,
    string? EvidenceArtifactId,
    DateTimeOffset OccurredAt,
    DateTimeOffset RecordedAt,
    string? Note);

public interface IApplicationCaseService
{
    IReadOnlyList<ApplicationCaseSummary> List();
    ApplicationCaseRecord Get(string caseId);
    ApplicationCaseRecord Create(ApplicationCaseCreateRequest request);
    ApplicationCaseRecord Update(ApplicationCaseUpdateRequest request);
    ApplicationCaseRecord ChangeStage(ApplicationStageChangeRequest request);
    ApplicationCaseRecord RecordSubmission(ApplicationSubmissionRequest request);
    IReadOnlyList<ApplicationEventRecord> Events(string caseId);
    ApplicationMaterialRecord AddMaterial(ApplicationMaterialCreateRequest request);
    ApplicationMaterialRecord UpdateMaterial(ApplicationMaterialUpdateRequest request);
    IReadOnlyList<ApplicationMaterialRecord> Materials(string caseId);
    ApplicationReminderRecord AddReminder(ApplicationReminderCreateRequest request);
    ApplicationReminderRecord UpdateReminder(ApplicationReminderUpdateRequest request);
    IReadOnlyList<ApplicationReminderRecord> Reminders(string? caseId = null);
    void LinkOutreach(string caseId, string outreachId);
    IReadOnlyList<string> OutreachIds(string caseId);
}
