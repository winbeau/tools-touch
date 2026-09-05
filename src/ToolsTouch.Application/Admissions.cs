using ToolsTouch.Core;

namespace ToolsTouch.Application;

public sealed record AdmissionImportOptions(
    int TargetCycleYear,
    string TimeZoneId = "Asia/Shanghai");

public sealed record AdmissionImportIssue(
    string ExternalId,
    string Code,
    string Detail,
    IReadOnlyList<string>? CandidateIds = null);

public sealed record AdmissionImportSummary(
    string ImportId,
    string Source,
    int Scanned,
    int Selected,
    int Imported,
    int Updated,
    int Unchanged,
    int Ambiguous,
    int Skipped,
    IReadOnlyList<AdmissionImportIssue> Issues,
    long DataRevision);

public interface IAdmissionImportService
{
    Task<AdmissionImportSummary> ImportAsync(
        CollectorResult result,
        AdmissionImportOptions options,
        CancellationToken cancellationToken = default);
}
