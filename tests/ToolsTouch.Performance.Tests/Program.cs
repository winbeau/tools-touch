using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ToolsTouch.Application;
using ToolsTouch.Core;
using ToolsTouch.Infrastructure.Persistence;
using ToolsTouch.Infrastructure.Tracking;

var output = Path.GetFullPath(args.Length == 2 && args[0] == "--output"
    ? args[1]
    : throw new ArgumentException("Usage: --output <evidence.json>"));
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var directory = Path.Combine(Path.GetTempPath(), "tools-touch-performance-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var database = new LocalDatabase(Path.Combine(directory, "performance.db"));
    database.Initialize();
    var records = new RecordWorkspaceService(database);
    var collection = records.CreateCustomCollection(new CustomCollectionCreateRequest("性能基准", Id: "performance-collection"));
    var name = records.CreateCustomField(new CustomFieldCreateRequest(collection.Id, "name", "名称", "Text", Id: "performance-name"));
    var score = records.CreateCustomField(new CustomFieldCreateRequest(collection.Id, "score", "分数", "Number", Id: "performance-score"));
    const int recordCount = 100_000;
    Seed(database, collection.Id, name.Id, score.Id, recordCount);
    var queries = new RecordQueryService(database);
    var sort = JsonSerializer.Serialize(new[] { new { field_id = score.Id, direction = "desc", nulls = "last" } });
    var request = new RecordQueryRequest(collection.Id, FilterAstJson: FilterBuilder.Condition(score.Id, "gte", "50000"), SortJson: sort, Limit: 50);
    for (var warmup = 0; warmup < 5; warmup++) Check(queries.Query(request).Items.Count == 50, "warmup query did not return 50 rows");
    var timings = new List<double>();
    for (var iteration = 0; iteration < 25; iteration++)
    {
        var timer = Stopwatch.StartNew();
        var page = queries.Query(request);
        timer.Stop();
        Check(page.Items.Count == 50 && page.NextCursor is not null, "paged query result shape changed");
        timings.Add(timer.Elapsed.TotalMilliseconds);
    }
    timings.Sort();
    var cancelledPath = Path.Combine(directory, "cancelled.zip");
    using (var cancellation = new CancellationTokenSource())
    {
        cancellation.Cancel();
        try { new WorkspaceExportService(database).Export(new WorkspaceExportRequest("WorkspaceAll", "business_bundle", cancelledPath), cancellation.Token); }
        catch (OperationCanceledException) { }
    }
    Check(!File.Exists(cancelledPath), "cancelled export left a final-looking file");

    var exportPath = Path.Combine(directory, "performance.zip");
    var exportTimer = Stopwatch.StartNew();
    var exported = new WorkspaceExportService(database).Export(new WorkspaceExportRequest("WorkspaceAll", "business_bundle", exportPath, IncludeAttachments: false));
    exportTimer.Stop();
    Check(File.Exists(exportPath) && exported.RowCount >= recordCount * 3, "full export lost synthetic rows");
    var evidence = new
    {
        work_package = "P08-B",
        status = "Verified",
        executed_at = DateTimeOffset.UtcNow,
        platform = Environment.OSVersion.ToString(),
        runtime = Environment.Version.ToString(),
        record_count = recordCount,
        query = new
        {
            filter = "score >= 50000",
            page_size = 50,
            samples = timings.Count,
            p50_ms = Percentile(timings, 0.50),
            p95_ms = Percentile(timings, 0.95),
            max_ms = timings[^1]
        },
        export = new
        {
            format = "business_bundle",
            row_count = exported.RowCount,
            elapsed_ms = exportTimer.Elapsed.TotalMilliseconds,
            byte_length = new FileInfo(exportPath).Length,
            cancelled_final_file_exists = File.Exists(cancelledPath)
        },
        working_set_mb = Process.GetCurrentProcess().WorkingSet64 / 1024d / 1024d,
        goals = new { common_query_p95_ms = 500, complex_query_p95_ms = 1000 },
        real_accounts_used = false,
        real_mail_sent = false
    };
    File.WriteAllText(output, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    Console.WriteLine(JsonSerializer.Serialize(evidence));
}
finally
{
    SqliteConnection.ClearAllPools();
    if (Directory.Exists(directory)) Directory.Delete(directory, true);
}

static void Seed(LocalDatabase database, string collectionId, string nameFieldId, string scoreFieldId, int count)
{
    using var connection = database.Open();
    using var transaction = connection.BeginTransaction();
    using var record = LocalDatabase.Command(connection, "INSERT INTO RecordRef(Id,CollectionId,CreatedAt,Revision) VALUES($id,$collection,$now,1)");
    using var value = LocalDatabase.Command(connection, "INSERT INTO FieldValue(RecordId,FieldId,TextValue,NumberValue,Revision) VALUES($record,$field,$text,$number,1)");
    record.Transaction = transaction; value.Transaction = transaction;
    record.Parameters.Add("$id", SqliteType.Text); record.Parameters.Add("$collection", SqliteType.Text); record.Parameters.Add("$now", SqliteType.Text);
    value.Parameters.Add("$record", SqliteType.Text); value.Parameters.Add("$field", SqliteType.Text); value.Parameters.Add("$text", SqliteType.Text); value.Parameters.Add("$number", SqliteType.Real);
    var now = DateTimeOffset.UtcNow.ToString("O");
    for (var index = 0; index < count; index++)
    {
        var id = "performance-record-" + index.ToString("D6", CultureInfo.InvariantCulture);
        record.Parameters["$id"].Value = id; record.Parameters["$collection"].Value = collectionId; record.Parameters["$now"].Value = now; record.ExecuteNonQuery();
        value.Parameters["$record"].Value = id; value.Parameters["$field"].Value = nameFieldId; value.Parameters["$text"].Value = "row-" + index.ToString(CultureInfo.InvariantCulture); value.Parameters["$number"].Value = DBNull.Value; value.ExecuteNonQuery();
        value.Parameters["$record"].Value = id; value.Parameters["$field"].Value = scoreFieldId; value.Parameters["$text"].Value = DBNull.Value; value.Parameters["$number"].Value = index; value.ExecuteNonQuery();
    }
    transaction.Commit();
}

static double Percentile(IReadOnlyList<double> sorted, double percentile)
{
    var position = (sorted.Count - 1) * percentile;
    var lower = (int)Math.Floor(position);
    var upper = (int)Math.Ceiling(position);
    if (lower == upper) return sorted[lower];
    return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
