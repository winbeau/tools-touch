using System.Text.Json.Serialization;

namespace ToolsTouch.Application;

public enum FacultyCoverageState
{
    CompleteForDeclaredSources,
    Partial,
    UnknownDenominator
}

public enum FacultyCrawlItemState
{
    Discovered,
    Fetched,
    Selected,
    Failed,
    Skipped
}

public sealed record FacultyDirectoryScope(
    string SchoolId,
    string DepartmentId,
    string SeedUrl,
    int? DeclaredFacultyCount = null,
    string? Label = null)
{
    [JsonIgnore]
    public string NormalizedSeedUrl => Uri.TryCreate(SeedUrl, UriKind.Absolute, out var uri)
        ? uri.AbsoluteUri
        : throw new ArgumentException("DIRECTORY_SEED_URL_REQUIRED", nameof(SeedUrl));
}

public sealed record SourceAdapterDescriptor(
    string SourceId,
    string AdapterVersion,
    string DisplayName,
    string[] SupportedPageKinds);

public sealed record FacultyCrawlTarget(
    string Url,
    string PageKind = "directory",
    string? ParentUrl = null,
    int Depth = 0)
{
    [JsonIgnore]
    public string NormalizedUrl => Uri.TryCreate(Url, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0
        ? uri.AbsoluteUri
        : throw new ArgumentException("DIRECTORY_TARGET_URL_INVALID", nameof(Url));
}

public sealed record FacultySourcePage(
    string RequestedUrl,
    string FinalUrl,
    string MediaType,
    byte[] Content,
    DateTimeOffset FetchedAt,
    string? ETag = null,
    string? LastModified = null);

public sealed record FacultyDirectoryCandidate(
    string? ExternalId,
    string Name,
    string? HomepageUrl,
    string? Role,
    string RawText,
    string SourceUrl,
    string? DepartmentName = null,
    string? PublicEmail = null,
    string? PublicEmailRaw = null);

public sealed record FacultyDirectoryPage(
    string RequestedUrl,
    string CanonicalUrl,
    IReadOnlyList<FacultyDirectoryCandidate> Candidates,
    IReadOnlyList<FacultyCrawlTarget> DiscoveredTargets,
    int? ExpectedCount = null,
    bool IsDirectoryPage = true);

public sealed record FacultyPagePayload(
    string SnapshotId,
    FacultyDirectoryCandidate[] Candidates,
    int? ExpectedCount,
    string CanonicalUrl);

public sealed record SourceAdapterHealth(
    bool Healthy,
    string State,
    string? Message = null);

public interface IFacultyDirectorySourceAdapter
{
    SourceAdapterDescriptor Descriptor { get; }
    IReadOnlyList<FacultyCrawlTarget> DiscoverScope(FacultyDirectoryScope scope);
    Task<FacultySourcePage> FetchAsync(FacultyCrawlTarget target, CancellationToken cancellationToken = default);
    FacultyDirectoryPage Parse(FacultyDirectoryScope scope, FacultySourcePage page);
    FacultyDirectoryCandidate Normalize(FacultyDirectoryCandidate candidate);
    Task<SourceAdapterHealth> HealthAsync(CancellationToken cancellationToken = default);
}

public sealed record FacultyCrawlOptions(
    string? ExistingBatchId = null,
    int MaxPages = 1_000,
    int MaxEntries = 100_000,
    TimeSpan? DelayBetweenRequests = null);

public sealed record FacultyCrawlFailure(
    string ItemId,
    string Url,
    string Code,
    string Message);

public sealed record FacultyCoverageReport(
    string BatchId,
    string SourceId,
    string SchoolId,
    string DepartmentId,
    FacultyCoverageState CoverageState,
    int DiscoveredPageCount,
    int FetchedPageCount,
    int ParsedEntryCount,
    int? ExpectedEntryCount,
    int FailedPageCount,
    int SkippedPageCount,
    IReadOnlyList<FacultyCrawlFailure> Failures,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    long DatasetRevision);

public interface IFacultyDirectoryCrawler
{
    Task<FacultyCoverageReport> CrawlAsync(
        IFacultyDirectorySourceAdapter adapter,
        FacultyDirectoryScope scope,
        FacultyCrawlOptions? options = null,
        CancellationToken cancellationToken = default);
}

public sealed record FacultyIdentityImportOptions(bool IncludePartialBatches = true);

public sealed record FacultyIdentityImportIssue(
    string ExternalId,
    string Code,
    string Message,
    IReadOnlyList<string> CandidateProfessorIds);

public sealed record FacultyIdentityImportSummary(
    string BatchId,
    int ImportedProfessorCount,
    int UpdatedProfessorCount,
    int UnchangedProfessorCount,
    int AppointmentCount,
    int EvidenceClaimCount,
    int AmbiguousCount,
    IReadOnlyList<FacultyIdentityImportIssue> Issues,
    long DatasetRevision);

public interface IFacultyIdentityImportService
{
    Task<FacultyIdentityImportSummary> ImportAsync(
        string batchId,
        FacultyIdentityImportOptions? options = null,
        CancellationToken cancellationToken = default);
}
