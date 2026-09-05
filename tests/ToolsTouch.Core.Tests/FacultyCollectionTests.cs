using System.Text;
using Microsoft.Data.Sqlite;
using ToolsTouch.Application;
using ToolsTouch.Core;
using ToolsTouch.Infrastructure.Collection;

static class FacultyCollectionTests
{
    public static async Task RunAsync(LocalDatabase database, string directory)
    {
        var artifacts = new ArtifactStore(database.ArtifactDirectory);
        var sources = new SourceRepository(database, artifacts);
        var crawler = new FacultyDirectoryCrawler(database, artifacts, sources);
        var scope = new FacultyDirectoryScope("school-faculty", "department-cs", "https://faculty.example.edu/computer?page=1", 2, "Computer faculty");

        using (var web = new PublicWeb())
        {
            var official = new OfficialFacultyDirectoryAdapter(web);
            var html = """
                <html><body data-total="1">
                  <article class="faculty-card" data-faculty-id="official-1" data-name="Ada Lovelace">
                    <a class="homepage" href="https://faculty.example.edu/ada">Ada's homepage</a>
                    <span class="role">Professor</span>
                  </article>
                  <a rel="next" href="/computer?page=2">下一页</a>
                  <a rel="next" href="https://outside.example.org/page=2">external</a>
                </body></html>
                """;
            var page = official.Parse(scope, new FacultySourcePage(scope.SeedUrl, scope.SeedUrl, "text/html",
                Encoding.UTF8.GetBytes(html), DateTimeOffset.UtcNow));
            Check(page.Candidates.Count == 1 && page.Candidates[0].ExternalId == "official-1" &&
                page.Candidates[0].HomepageUrl == "https://faculty.example.edu/ada" && page.DiscoveredTargets.Count == 1,
                $"official directory HTML fixture extracts faculty entries and same-origin pagination only: candidates={page.Candidates.Count}, id={page.Candidates.FirstOrDefault()?.ExternalId}, homepage={page.Candidates.FirstOrDefault()?.HomepageUrl}, targets={page.DiscoveredTargets.Count}");
            Check(official.Normalize(page.Candidates[0]).Name == "Ada Lovelace", "directory candidate normalization is stable");
        }

        var adapter = new FixtureAdapter(failPage2: true);

        var first = await crawler.CrawlAsync(adapter, scope, new FacultyCrawlOptions(MaxPages: 10));
        Check(first.CoverageState == FacultyCoverageState.Partial && first.FetchedPageCount == 1 && first.FailedPageCount == 1,
            "failed directory pages remain visible in a partial coverage report");
        Check(first.ParsedEntryCount == 1 && first.ExpectedEntryCount == 2, "directory entry and declared denominator are retained");

        adapter.FailPage2 = false;
        var second = await crawler.CrawlAsync(adapter, scope, new FacultyCrawlOptions(ExistingBatchId: first.BatchId, MaxPages: 10));
        Check(second.CoverageState == FacultyCoverageState.CompleteForDeclaredSources && second.ParsedEntryCount == 2 && second.FailedPageCount == 0,
            "resuming a failed page completes declared directory coverage");
        Check(adapter.FetchCount("https://faculty.example.edu/computer?page=1") == 1 &&
            adapter.FetchCount("https://faculty.example.edu/computer?page=2") == 2,
            "already submitted directory pages are not fetched again while failed pages are retried");
        Check(Count(database, "SELECT COUNT(*) FROM CrawlItem WHERE BatchId=$batch AND State='Fetched'", ("$batch", first.BatchId)) == 2,
            "crawl checkpoint stores both submitted page items");
        Check(Count(database, "SELECT COUNT(*) FROM SourceSnapshot WHERE SourceKey=$source", ("$source", adapter.Descriptor.SourceId)) == 2,
            "directory pages are retained as immutable source snapshots");
        Check(Count(database, "SELECT COUNT(*) FROM CrawlBatch WHERE Id=$batch AND ManifestArtifactId IS NOT NULL", ("$batch", first.BatchId)) == 1,
            "coverage report is published as an artifact linked to the crawl batch");

        var changedAdapter = new FixtureAdapter(failPage2: false, contentVersion: "changed");
        var changedScope = scope with { DepartmentId = "department-cs-2" };
        var changed = await crawler.CrawlAsync(changedAdapter, changedScope, new FacultyCrawlOptions(MaxPages: 10));
        Check(changed.CoverageState == FacultyCoverageState.CompleteForDeclaredSources &&
            Count(database, "SELECT COUNT(*) FROM SourceCheck WHERE State='Changed' AND SnapshotId IN (SELECT Id FROM SourceSnapshot WHERE SourceKey=$source)", ("$source", adapter.Descriptor.SourceId)) >= 1,
            "changed directory content appends a snapshot and records a changed source check");

        var unknownAdapter = new FixtureAdapter(failPage2: false, includeExpectedCount: false);
        var unknown = await crawler.CrawlAsync(unknownAdapter,
            scope with { DepartmentId = "department-cs-3", DeclaredFacultyCount = null }, new FacultyCrawlOptions(MaxPages: 10));
        Check(unknown.CoverageState == FacultyCoverageState.UnknownDenominator,
            "directory without a reported total is marked unknown denominator");
        Console.WriteLine("PASS: faculty adapter parsing contract, paged crawl checkpoints, immutable page snapshots and coverage report");
    }

    private sealed class FixtureAdapter(bool failPage2, string contentVersion = "one", bool includeExpectedCount = true)
        : IFacultyDirectorySourceAdapter
    {
        private readonly Dictionary<string, int> fetches = new(StringComparer.OrdinalIgnoreCase);
        public bool FailPage2 { get; set; } = failPage2;
        public SourceAdapterDescriptor Descriptor { get; } = new("official-faculty-directory", "fixture-directory-v1", "Fixture directory", ["directory"]);

        public IReadOnlyList<FacultyCrawlTarget> DiscoverScope(FacultyDirectoryScope scope) =>
            [new FacultyCrawlTarget(scope.NormalizedSeedUrl)];

        public Task<FacultySourcePage> FetchAsync(FacultyCrawlTarget target, CancellationToken cancellationToken = default)
        {
            var url = target.NormalizedUrl;
            fetches[url] = FetchCount(url) + 1;
            if (url.Contains("page=2", StringComparison.OrdinalIgnoreCase) && FailPage2)
                throw new HttpRequestException("fixture page unavailable");
            var content = Encoding.UTF8.GetBytes(url.Contains("page=1", StringComparison.OrdinalIgnoreCase)
                ? "<html><body>directory-one-" + contentVersion + "</body></html>"
                : "<html><body>directory-two-" + contentVersion + "</body></html>");
            return Task.FromResult(new FacultySourcePage(url, url, "text/html", content, DateTimeOffset.UtcNow));
        }

        public FacultyDirectoryPage Parse(FacultyDirectoryScope scope, FacultySourcePage page)
        {
            var first = page.FinalUrl.Contains("page=1", StringComparison.OrdinalIgnoreCase);
            var candidate = first
                ? new FacultyDirectoryCandidate("prof-alice", " Alice ", "https://faculty.example.edu/alice", "Professor", " Alice Professor ", page.FinalUrl)
                : new FacultyDirectoryCandidate("prof-bob", "Bob", null, "Associate Professor", "Bob", page.FinalUrl);
            IReadOnlyList<FacultyCrawlTarget> targets = first
                ? [new FacultyCrawlTarget("https://faculty.example.edu/computer?page=2", ParentUrl: page.FinalUrl)]
                : [];
            return new FacultyDirectoryPage(page.RequestedUrl, page.FinalUrl, [candidate], targets,
                includeExpectedCount ? 2 : null);
        }

        public FacultyDirectoryCandidate Normalize(FacultyDirectoryCandidate candidate) => candidate with
        {
            Name = candidate.Name.Trim(),
            ExternalId = candidate.ExternalId?.Trim()
        };

        public Task<SourceAdapterHealth> HealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SourceAdapterHealth(true, "Ready"));

        public int FetchCount(string url) => fetches.TryGetValue(url, out var value) ? value : 0;
    }

    private static int Count(LocalDatabase database, string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, sql, parameters);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
