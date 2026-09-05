using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ToolsTouch.Application;
using ToolsTouch.Core;

namespace ToolsTouch.Infrastructure.Collection;

public sealed class FacultyDirectoryCrawler(
    LocalDatabase database,
    ArtifactStore artifacts,
    ISourceRepository sources) : IFacultyDirectoryCrawler
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<FacultyCoverageReport> CrawlAsync(
        IFacultyDirectorySourceAdapter adapter,
        FacultyDirectoryScope scope,
        FacultyCrawlOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(scope);
        options ??= new FacultyCrawlOptions();
        if (options.MaxPages is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(options.MaxPages));
        if (options.MaxEntries is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(options.MaxEntries));
        if (options.DelayBetweenRequests is { } delay && (delay < TimeSpan.Zero || delay > TimeSpan.FromMinutes(1)))
            throw new ArgumentOutOfRangeException(nameof(options.DelayBetweenRequests));

        var health = await adapter.HealthAsync(cancellationToken);
        if (!health.Healthy) throw new InvalidOperationException("SOURCE_ADAPTER_UNHEALTHY:" + health.State);

        var startedAt = DateTimeOffset.UtcNow;
        var batchId = PrepareBatch(adapter, scope, options.ExistingBatchId);
        var processedPages = 0;
        var processedItems = new HashSet<string>(StringComparer.Ordinal);
        while (processedPages < options.MaxPages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = NextItem(batchId, processedItems);
            if (item is null) break;
            processedPages++;
            processedItems.Add(item.Id);
            UpdateBatchState(batchId, "Running", null);
            await ProcessItem(adapter, scope, batchId, item, options, cancellationToken);
            if (options.DelayBetweenRequests is { } requestDelay && requestDelay > TimeSpan.Zero)
                await Task.Delay(requestDelay, cancellationToken);
        }

        if (processedPages >= options.MaxPages && NextItem(batchId, processedItems) is not null)
            UpdateBatchState(batchId, "Completed", "MAX_PAGES_REACHED");

        var report = BuildReport(batchId, adapter.Descriptor.SourceId, scope, startedAt, DateTimeOffset.UtcNow);
        PublishReport(report);
        return report;
    }

    private async Task ProcessItem(
        IFacultyDirectorySourceAdapter adapter,
        FacultyDirectoryScope scope,
        string batchId,
        CrawlItem item,
        FacultyCrawlOptions options,
        CancellationToken cancellationToken)
    {
        IncrementAttempt(item.Id);
        try
        {
            var target = JsonSerializer.Deserialize<FacultyCrawlTarget>(item.PayloadJson, JsonOptions)
                ?? throw new InvalidOperationException("CRAWL_TARGET_PAYLOAD_INVALID");
            var page = await adapter.FetchAsync(target, cancellationToken);
            if (page.Content.Length > 2_000_000) throw new InvalidOperationException("DIRECTORY_PAGE_TOO_LARGE");
            var parsed = adapter.Parse(scope, page);
            var candidates = parsed.Candidates.Select(adapter.Normalize).ToArray();
            if (candidates.Length > options.MaxEntries) throw new InvalidOperationException("DIRECTORY_ENTRY_LIMIT");
            var snapshot = SavePageSnapshot(adapter, page);
            var payload = new FacultyPagePayload(snapshot.Id, candidates, parsed.ExpectedCount, page.FinalUrl);
            MarkFetched(item.Id, JsonSerializer.Serialize(payload, JsonOptions));

            foreach (var discovered in parsed.DiscoveredTargets)
            {
                if (discovered.Depth > 50) continue;
                AddItem(batchId, discovered);
            }
            UpdateBatchExpected(batchId, parsed.ExpectedCount ?? scope.DeclaredFacultyCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            MarkFailed(item.Id, ErrorCode(error), error.Message);
        }
    }

    private SourceSnapshot SavePageSnapshot(IFacultyDirectorySourceAdapter adapter, FacultySourcePage page)
    {
        var canonical = PublicWeb.ValidateUri(page.FinalUrl).AbsoluteUri;
        var contentHash = Convert.ToHexString(SHA256.HashData(page.Content)).ToLowerInvariant();
        var previous = LatestSnapshot(adapter.Descriptor.SourceId, canonical);
        if (previous is not null && previous.ContentHash.Equals(contentHash, StringComparison.OrdinalIgnoreCase))
        {
            sources.AddSourceCheck(new SourceCheck(
                StableId("check", adapter.Descriptor.SourceId + "|" + canonical + "|" + page.FetchedAt.ToUniversalTime().ToString("O")),
                previous.Id, page.FetchedAt, "Unchanged"));
            return previous;
        }

        var snapshotId = StableId("snapshot", adapter.Descriptor.SourceId + "|" + canonical + "|" + contentHash + "|" + page.FetchedAt.ToUniversalTime().ToString("O"));
        var snapshot = sources.SaveSnapshot(
            new SourceSnapshot(snapshotId, adapter.Descriptor.SourceId, page.RequestedUrl, canonical, canonical, contentHash,
                page.FetchedAt, null, null, "https", adapter.Descriptor.AdapterVersion, title: null),
            page.Content, "faculty-directory-page", page.MediaType, ".html");
        sources.AddSourceCheck(new SourceCheck(
            StableId("check", snapshot.Id), snapshot.Id, page.FetchedAt,
            previous is null ? "Fetched" : "Changed", page.ETag, page.LastModified));
        return snapshot;
    }

    private string PrepareBatch(IFacultyDirectorySourceAdapter adapter, FacultyDirectoryScope scope, string? existingBatchId)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        if (existingBatchId is not null)
        {
            using var existing = LocalDatabase.Command(connection,
                "SELECT SourceKey,ScopeJson FROM CrawlBatch WHERE Id=$id", ("$id", existingBatchId));
            existing.Transaction = transaction;
            using var reader = existing.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException("CRAWL_BATCH_NOT_FOUND");
            if (!reader.GetString(0).Equals(adapter.Descriptor.SourceId, StringComparison.Ordinal) ||
                !reader.GetString(1).Equals(JsonSerializer.Serialize(scope, JsonOptions), StringComparison.Ordinal))
                throw new InvalidOperationException("CRAWL_BATCH_SCOPE_CONFLICT");
            reader.Close();
            SetBatchState(connection, transaction, existingBatchId, "Running", null, now);
            transaction.Commit();
            return existingBatchId;
        }

        var batchId = StableId("batch", adapter.Descriptor.SourceId + "|" + scope.SchoolId + "|" + scope.DepartmentId + "|" + scope.NormalizedSeedUrl + "|" + now);
        using (var insert = LocalDatabase.Command(connection, """
            INSERT INTO CrawlBatch(Id,SourceKey,ScopeJson,State,ExpectedCount,Complete,CreatedAt,UpdatedAt)
            VALUES($id,$source,$scope,'Queued',$expected,0,$now,$now)
            """, ("$id", batchId), ("$source", adapter.Descriptor.SourceId),
            ("$scope", JsonSerializer.Serialize(scope, JsonOptions)), ("$expected", scope.DeclaredFacultyCount), ("$now", now)))
        {
            insert.Transaction = transaction;
            insert.ExecuteNonQuery();
        }
        foreach (var target in adapter.DiscoverScope(scope)) InsertItem(connection, transaction, batchId, target, now);
        SetBatchState(connection, transaction, batchId, "Running", null, now);
        transaction.Commit();
        return batchId;
    }

    private CrawlItem? NextItem(string batchId, ISet<string>? excluded = null)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            SELECT Id,ExternalId,State,AttemptCount,PayloadJson,Error
            FROM CrawlItem
            WHERE BatchId=$batch AND State IN ('Discovered','Failed') AND AttemptCount < 3
            ORDER BY Id
            """, ("$batch", batchId));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            if (excluded?.Contains(id) == true) continue;
            return new CrawlItem(id, reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5));
        }
        return null;
    }

    private void IncrementAttempt(string itemId)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection,
            "UPDATE CrawlItem SET AttemptCount=AttemptCount+1,UpdatedAt=$now WHERE Id=$id", ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$id", itemId));
        command.ExecuteNonQuery();
    }

    private void MarkFetched(string itemId, string payload)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection,
            "UPDATE CrawlItem SET State='Fetched',PayloadJson=$payload,Error=NULL,UpdatedAt=$now WHERE Id=$id",
            ("$payload", payload), ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$id", itemId));
        command.ExecuteNonQuery();
    }

    private void MarkFailed(string itemId, string code, string message)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection,
            "UPDATE CrawlItem SET State='Failed',Error=$error,UpdatedAt=$now WHERE Id=$id",
            ("$error", code + ":" + message[..Math.Min(500, message.Length)]), ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$id", itemId));
        command.ExecuteNonQuery();
    }

    private void AddItem(string batchId, FacultyCrawlTarget target)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        InsertItem(connection, transaction, batchId, target, DateTimeOffset.UtcNow.ToString("O"));
        transaction.Commit();
    }

    private static void InsertItem(SqliteConnection connection, SqliteTransaction transaction, string batchId,
        FacultyCrawlTarget target, string now)
    {
        var url = target.NormalizedUrl;
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO CrawlItem(Id,BatchId,ExternalId,State,PayloadJson,UpdatedAt)
            VALUES($id,$batch,$external,'Discovered',$payload,$now)
            ON CONFLICT(BatchId,ExternalId) DO NOTHING
            """, ("$id", StableId("item", batchId + "|" + url)), ("$batch", batchId), ("$external", url),
            ("$payload", JsonSerializer.Serialize(target, JsonOptions)), ("$now", now));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private void UpdateBatchExpected(string batchId, int? expected)
    {
        if (expected is null) return;
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection,
            "UPDATE CrawlBatch SET ExpectedCount=COALESCE(ExpectedCount,$expected),UpdatedAt=$now WHERE Id=$id",
            ("$expected", expected), ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$id", batchId));
        command.ExecuteNonQuery();
    }

    private void UpdateBatchState(string batchId, string state, string? error)
    {
        using var connection = database.Open();
        SetBatchState(connection, null, batchId, state, error, DateTimeOffset.UtcNow.ToString("O"));
    }

    private static void SetBatchState(SqliteConnection connection, SqliteTransaction? transaction, string batchId,
        string state, string? error, string now)
    {
        using var command = LocalDatabase.Command(connection,
            "UPDATE CrawlBatch SET State=$state,Error=$error,UpdatedAt=$now WHERE Id=$id",
            ("$state", state), ("$error", error), ("$now", now), ("$id", batchId));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private FacultyCoverageReport BuildReport(string batchId, string sourceId, FacultyDirectoryScope scope,
        DateTimeOffset startedAt, DateTimeOffset finishedAt)
    {
        using var connection = database.Open();
        using var batch = LocalDatabase.Command(connection,
            "SELECT ExpectedCount FROM CrawlBatch WHERE Id=$id", ("$id", batchId));
        using var batchReader = batch.ExecuteReader();
        if (!batchReader.Read()) throw new KeyNotFoundException("CRAWL_BATCH_NOT_FOUND");
        var expected = batchReader.IsDBNull(0) ? (int?)null : batchReader.GetInt32(0);
        batchReader.Close();

        var failures = new List<FacultyCrawlFailure>();
        var fetched = 0;
        var skipped = 0;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        using var items = LocalDatabase.Command(connection,
            "SELECT Id,ExternalId,State,PayloadJson,Error FROM CrawlItem WHERE BatchId=$batch ORDER BY Id", ("$batch", batchId));
        using var reader = items.ExecuteReader();
        while (reader.Read())
        {
            var state = reader.GetString(2);
            if (state is "Fetched" or "Selected")
            {
                fetched++;
                if (!reader.IsDBNull(3)) AddEntryIds(ids, reader.GetString(3));
            }
            else if (state == "Skipped") skipped++;
            if (state == "Failed")
                failures.Add(new FacultyCrawlFailure(reader.GetString(0), reader.GetString(1), "PAGE_FAILED", reader.IsDBNull(4) ? "Page failed." : reader.GetString(4)));
        }
        reader.Close();
        var failed = failures.Count;
        var effectiveExpected = expected ?? scope.DeclaredFacultyCount;
        var pending = CountPending(connection, batchId);
        var coverage = failed > 0 || pending > 0 ? FacultyCoverageState.Partial :
            effectiveExpected is null ? FacultyCoverageState.UnknownDenominator :
            ids.Count == effectiveExpected ? FacultyCoverageState.CompleteForDeclaredSources : FacultyCoverageState.Partial;
        var report = new FacultyCoverageReport(batchId, sourceId, scope.SchoolId, scope.DepartmentId, coverage,
            CountItems(connection, batchId), fetched, ids.Count, effectiveExpected, failed, skipped, failures, startedAt, finishedAt,
            new OrganizationRepository(database).Get().DataRevision);
        UpdateBatchCompletion(report, failed > 0 ? "PAGES_FAILED" : pending > 0 ? "CRAWL_INCOMPLETE" : null);
        return report;
    }

    private void PublishReport(FacultyCoverageReport report)
    {
        var content = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(report, JsonOptions));
        var artifact = artifacts.Stage("faculty-coverage", content, "application/json", ".json");
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        ArtifactStore.Insert(connection, transaction, artifact);
        using var update = LocalDatabase.Command(connection,
            "UPDATE CrawlBatch SET ManifestArtifactId=$artifact,UpdatedAt=$now WHERE Id=$id",
            ("$artifact", artifact.Id), ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$id", report.BatchId));
        update.Transaction = transaction;
        update.ExecuteNonQuery();
        transaction.Commit();
    }

    private void UpdateBatchCompletion(FacultyCoverageReport report, string? error)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            UPDATE CrawlBatch SET State=$state,Complete=$complete,ScannedCount=$scanned,SelectedCount=$selected,
              ExpectedCount=$expected,Error=$error,UpdatedAt=$now WHERE Id=$id
            """, ("$state", "Completed"), ("$complete", report.CoverageState == FacultyCoverageState.CompleteForDeclaredSources ? 1 : 0),
            ("$scanned", report.FetchedPageCount), ("$selected", report.ParsedEntryCount), ("$expected", report.ExpectedEntryCount),
            ("$error", error), ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$id", report.BatchId));
        command.ExecuteNonQuery();
    }

    private static int CountPending(SqliteConnection connection, string batchId)
    {
        using var command = LocalDatabase.Command(connection,
            "SELECT COUNT(*) FROM CrawlItem WHERE BatchId=$batch AND State IN ('Discovered','Failed') AND AttemptCount < 3", ("$batch", batchId));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private SourceSnapshot? LatestSnapshot(string sourceId, string canonicalUrl)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            SELECT Id,SourceKey,OriginalUrl,CanonicalUrl,ExternalRecordId,ContentHash,FetchedAt,PublishedAt,SourceYear,Transport,ParseVersion,ArtifactId,Title
            FROM SourceSnapshot WHERE SourceKey=$source AND ExternalRecordId=$external ORDER BY FetchedAt DESC,Id DESC LIMIT 1
            """, ("$source", sourceId), ("$external", canonicalUrl));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new SourceSnapshot(reader.GetString(0), reader.GetString(1), Nullable(reader, 2), Nullable(reader, 3), Nullable(reader, 4),
            reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6)), NullableDate(reader, 7), NullableInt(reader, 8), reader.GetString(9),
            reader.GetString(10), Nullable(reader, 11), Nullable(reader, 12));
    }

    private static void AddEntryIds(HashSet<string> ids, string payload)
    {
        var stored = JsonSerializer.Deserialize<FacultyPagePayload>(payload, JsonOptions);
        if (stored is null) return;
        foreach (var candidate in stored.Candidates)
            if (!string.IsNullOrWhiteSpace(candidate.ExternalId)) ids.Add(candidate.ExternalId);
    }

    private static int CountItems(SqliteConnection connection, string batchId)
    {
        using var command = LocalDatabase.Command(connection, "SELECT COUNT(*) FROM CrawlItem WHERE BatchId=$batch", ("$batch", batchId));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string ErrorCode(Exception error) => error is HttpRequestException ? "FETCH_FAILED" : error.Message.Split(':', 2)[0];
    private static string StableId(string prefix, string value) => prefix + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..32];
    private static string? Nullable(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static int? NullableInt(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt32(index);
    private static DateTimeOffset? NullableDate(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : DateTimeOffset.Parse(reader.GetString(index));

    private sealed record CrawlItem(string Id, string ExternalId, string State, int AttemptCount, string PayloadJson, string? Error);
}
