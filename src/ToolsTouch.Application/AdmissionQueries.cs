using ToolsTouch.Core;

namespace ToolsTouch.Application;

public sealed record AdmissionRoundRow(
    string RoundId,
    string SchoolId,
    string SchoolName,
    string DepartmentId,
    string DepartmentName,
    string ProgramId,
    string ProgramName,
    string DegreeType,
    AdmissionRoundKind Kind,
    string RoundKey,
    string Title,
    string? OfficialUrl,
    string? ApplicationUrl,
    WindowAssessment Assessment);

public sealed record SchoolOverviewRow(
    string SchoolId,
    string SchoolName,
    int OpenDepartmentCount,
    int OpenRoundCount,
    int UpcomingDepartmentCount,
    int ForecastDepartmentCount,
    int UnknownRoundCount,
    int ClosedRoundCount,
    int RecordedRoundCount,
    bool AllRecordedRoundsClosed,
    long DatasetRevision);

public interface IAdmissionQueryService
{
    PageResult<SchoolOverviewRow> ListSchools(int targetCycleYear, string timeZoneId, DateTimeOffset asOf, PageRequest request);
    PageResult<AdmissionRoundRow> ListRounds(string schoolId, int targetCycleYear, string timeZoneId, DateTimeOffset asOf, PageRequest request);
    string ExportRoundsCsv(string schoolId, int targetCycleYear, string timeZoneId, DateTimeOffset asOf);
}
