using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ToolsTouch.Application;
using ToolsTouch.Core;
using ToolsTouch.Infrastructure.Collection;

static class AdmissionImportTests
{
    public static async Task RunAsync(LocalDatabase database, string directory)
    {
        var root = Path.Combine(directory, "admission-import");
        CreateStaging(root, "示例大学", "示例大学", includeUnknownYear: true);
        var manifest = CollectorManifestValidator.ReadAndValidate(root, new CollectorLimits(MaxRecords: 10, MaxBytes: 100_000));
        var result = new CollectorResult(root, manifest);
        var importer = new AdmissionImportService(database, new ArtifactStore(database.ArtifactDirectory));
        var first = await importer.ImportAsync(result, new AdmissionImportOptions(2026));
        Check(first.Imported == 2 && first.Ambiguous == 0 && first.Skipped == 0, "admission records should import into typed entities");
        var schoolCount = Count(database, "SELECT COUNT(*) FROM School WHERE CanonicalName='示例大学'");
        var departmentCount = Count(database, "SELECT COUNT(*) FROM Department WHERE CanonicalName='计算机学院'");
        var roundCount = Count(database, "SELECT COUNT(*) FROM ExternalIdentity WHERE SourceKey='baoyan' AND EntityKind='AdmissionRound'");
        var observationCount = Count(database, "SELECT COUNT(*) FROM WindowObservation JOIN SourceSnapshot ON SourceSnapshot.Id=WindowObservation.SourceSnapshotId WHERE SourceSnapshot.SourceKey='baoyan'");
        var snapshotCount = Count(database, "SELECT COUNT(*) FROM SourceSnapshot WHERE SourceKey='baoyan'");
        Check(schoolCount == 1 && departmentCount == 1 && roundCount == 2 && observationCount == 2 && snapshotCount == 2,
            $"admission import row counts were unexpected: school={schoolCount}, department={departmentCount}, round={roundCount}, observation={observationCount}, snapshot={snapshotCount}");
        Check(Count(database, "SELECT COUNT(*) FROM WindowObservation WHERE SourceYear=2025 AND CycleYear=2025 AND EntryYear=2027 AND Precision='Date'") == 1 &&
            Count(database, "SELECT COUNT(*) FROM WindowObservation WHERE SourceYear IS NULL AND CycleYear IS NULL") == 1,
            "source year, resolved cycle year and unknown year must remain separate");
        var query = new AdmissionQueryService(database);
        var overview = query.ListSchools(2026, "Asia/Shanghai", DateTimeOffset.Parse("2026-09-15T10:00:00+08:00"), new PageRequest(200));
        var overviewRow = overview.Items.Single(row => row.SchoolName == "示例大学");
        Check(overviewRow.ForecastDepartmentCount == 1 && overviewRow.UnknownRoundCount == 1,
            $"school overview counts were unexpected: forecast={overviewRow.ForecastDepartmentCount}, unknown={overviewRow.UnknownRoundCount}");
        var schoolId = Scalar(database, "SELECT Id FROM School WHERE CanonicalName='示例大学'");
        var firstPage = query.ListRounds(schoolId, 2026, "Asia/Shanghai", DateTimeOffset.Parse("2026-09-15T10:00:00+08:00"), new PageRequest(1));
        var secondPage = query.ListRounds(schoolId, 2026, "Asia/Shanghai", DateTimeOffset.Parse("2026-09-15T10:00:00+08:00"), new PageRequest(1, firstPage.NextCursor));
        Check(firstPage.Items.Count == 1 && secondPage.Items.Count == 1 && firstPage.Items[0].RoundId != secondPage.Items[0].RoundId &&
            firstPage.Items.Concat(secondPage.Items).Any(row => row.Assessment.EstimatedPhase == EstimatedWindowPhase.WithinEstimatedRange),
            "school detail must expose stable paged rounds and shared window assessment");
        var csv = query.ExportRoundsCsv(schoolId, 2026, "Asia/Shanghai", DateTimeOffset.Parse("2026-09-15T10:00:00+08:00"));
        Check(csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 3 && csv.StartsWith("school,department,program") && csv.Contains("WithinEstimatedRange"),
            "admission CSV export must include all round rows and assessed state");
        var revision = first.DataRevision;
        var second = await importer.ImportAsync(result, new AdmissionImportOptions(2026));
        Check(second.Unchanged == 2 && second.Imported == 0 && second.DataRevision == revision,
            "repeating the same manifest must be idempotent without a data revision");

        var ambiguousRoot = Path.Combine(directory, "admission-ambiguous");
        CreateStaging(ambiguousRoot, "歧义大学", "歧义大学", department: "歧义学院");
        var organizations = new OrganizationRepository(database);
        organizations.AddSchool(new School("ambiguous-school-a", "歧义大学", "歧义大学"));
        organizations.AddSchool(new School("ambiguous-school-b", "歧义大学", "歧义大学"));
        var ambiguousManifest = CollectorManifestValidator.ReadAndValidate(ambiguousRoot, new CollectorLimits(MaxRecords: 10, MaxBytes: 100_000));
        var ambiguousResult = await importer.ImportAsync(new CollectorResult(ambiguousRoot, ambiguousManifest), new AdmissionImportOptions(2026));
        Check(ambiguousResult.Ambiguous == 1 && ambiguousResult.Imported == 0 &&
            Count(database, "SELECT COUNT(*) FROM EntityResolution WHERE State='Pending'") >= 1 &&
            Count(database, "SELECT COUNT(*) FROM ExternalIdentity WHERE SourceKey='baoyan' AND EntityKind='AdmissionRound'") == 2,
            "ambiguous school names must enter resolution without merging a round");

        var beforeInvalid = Count(database, "SELECT COUNT(*) FROM AdmissionRound");
        var badManifest = JsonSerializer.Deserialize<Dictionary<string, object?>>(File.ReadAllText(Path.Combine(root, "manifest.json")))!;
        badManifest["complete"] = false;
        File.WriteAllText(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(badManifest));
        Expect("COLLECTOR_INCOMPLETE", () => importer.ImportAsync(result, new AdmissionImportOptions(2026)).GetAwaiter().GetResult());
        Check(Count(database, "SELECT COUNT(*) FROM AdmissionRound") == beforeInvalid,
            "invalid collector batches must leave the last published import intact");
        Console.WriteLine("PASS: admission import staging, source/cycle separation, idempotency, ambiguity queue and failed-batch isolation");
    }

    private static void CreateStaging(string root, string school, string recordSchool, bool includeUnknownYear = false, string department = "计算机学院")
    {
        Directory.CreateDirectory(Path.Combine(root, "raw"));
        File.WriteAllText(Path.Combine(root, "raw", "page.json"), "{\"content\":[{\"id\":\"one\"},{\"id\":\"two\"}]}\n", Encoding.UTF8);
        var records = new List<Dictionary<string, object?>> { Record("one", recordSchool, department, 2025, "2027级预推免通知", "2025-09-12", "2025-09-18") };
        if (includeUnknownYear) records.Add(Record("two", recordSchool, department, null, "年份待核实的预推免通知", null, null));
        File.WriteAllLines(Path.Combine(root, "records.jsonl"), records.Select(record => JsonSerializer.Serialize(record)), Encoding.UTF8);
        var files = new[] { FileEntry(root, "records.jsonl", "records"), FileEntry(root, "raw/page.json", "raw") };
        var manifest = new Dictionary<string, object?>
        {
            ["schema_version"] = 1, ["source"] = "baoyan", ["adapter_version"] = "baoyan-http-0.1.0",
            ["scope"] = new Dictionary<string, object?> { ["school"] = school, ["kind"] = "预推免", ["year"] = null },
            ["fetched_at"] = "2026-09-05T00:00:00Z", ["complete"] = true,
            ["counts"] = new Dictionary<string, object?> { ["scanned"] = records.Count, ["expected"] = records.Count, ["selected"] = records.Count },
            ["files"] = files
        };
        File.WriteAllText(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(manifest), Encoding.UTF8);
    }

    private static Dictionary<string, object?> Record(string id, string school, string department, int? year, string title, string? start, string? end) => new()
    {
        ["external_id"] = id, ["school"] = school, ["department"] = department, ["source_year"] = year,
        ["kind"] = "PreRecommendation", ["title"] = title, ["registration_start_raw"] = start, ["registration_end_raw"] = end,
        ["event_start_raw"] = null, ["event_end_raw"] = null, ["official_url"] = "https://example.edu/official",
        ["application_url"] = "https://example.edu/apply", ["source_url"] = "https://example.edu/source/" + id,
        ["raw_ref"] = new Dictionary<string, object?> { ["path"] = "raw/page.json", ["json_pointer"] = "/content/0" }
    };

    private static Dictionary<string, object?> FileEntry(string root, string relative, string kind)
    {
        var bytes = File.ReadAllBytes(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return new() { ["path"] = relative, ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), ["byte_length"] = bytes.Length, ["kind"] = kind };
    }

    private static int Count(LocalDatabase database, string sql)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string Scalar(LocalDatabase database, string sql)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }

    private static void Expect(string code, Action action)
    {
        try { action(); throw new Exception("Expected " + code); }
        catch (InvalidOperationException error) when (error.Message == code) { }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
