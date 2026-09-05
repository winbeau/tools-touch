using ToolsTouch.Core;

namespace ToolsTouch.Application;

public sealed record FacultyListRow(
    string ProfessorId,
    string Name,
    string Institution,
    string? Homepage,
    string? Email,
    IReadOnlyList<string> DepartmentNames,
    int AppointmentCount,
    DateTimeOffset? LastEvidenceAt);

public sealed record FacultyAppointmentRow(
    string Id,
    string DepartmentName,
    string SchoolName,
    string? Title,
    string? Role,
    bool IsPrimary,
    string? EvidenceClaimId);

public sealed record FacultyEvidenceRow(
    string Id,
    string ClaimType,
    string ValueJson,
    string? QuotedText,
    string? LocatorJson,
    string OriginKind,
    string ConfidenceLabel);

public sealed record FacultyEvaluationRow(
    string Id,
    string SourceType,
    DateTimeOffset? PostedAt,
    string Summary,
    string TopicsJson,
    string VerificationState);

public sealed record FacultyPaperReadRow(
    string Id,
    string? ContentHash,
    int? StartPage,
    int? EndPage,
    string ReadScope,
    string ExtractorVersion,
    string? TextArtifactId,
    DateTimeOffset CreatedAt);

public sealed record FacultyPaperRow(
    Paper Paper,
    IReadOnlyList<FacultyPaperReadRow> Reads);

public sealed record FacultyDetail(
    FacultyListRow Faculty,
    IReadOnlyList<FacultyAppointmentRow> Appointments,
    IReadOnlyList<FacultyPaperRow> Papers,
    IReadOnlyList<FacultyEvidenceRow> Evidence,
    IReadOnlyList<FacultyEvaluationRow> Evaluations);

public sealed record FacultyCoverageRow(
    string BatchId,
    string SchoolId,
    string DepartmentId,
    string State,
    FacultyCoverageState CoverageState,
    int DiscoveredPageCount,
    int FetchedPageCount,
    int ParsedEntryCount,
    int? ExpectedEntryCount,
    string? Error,
    DateTimeOffset UpdatedAt);

public interface IFacultyQueryService
{
    PageResult<FacultyListRow> ListFaculty(string? filter, PageRequest request);
    FacultyDetail GetDetail(string professorId);
    IReadOnlyList<FacultyCoverageRow> ListCoverage();
}
