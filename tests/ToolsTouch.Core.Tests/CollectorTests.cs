using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ToolsTouch.Application;
using ToolsTouch.Infrastructure.Collection;

static class CollectorTests
{
    public static Task RunAsync(string directory)
    {
        var root = Path.Combine(directory, "collector-fixture");
        Directory.CreateDirectory(Path.Combine(root, "raw"));
        File.WriteAllText(Path.Combine(root, "raw", "response.json"), "{\"content\":[{}]}\n", Encoding.UTF8);
        var record = new Dictionary<string, object?>
        {
            ["external_id"] = "external-1", ["school"] = "示例大学", ["department"] = "计算机学院", ["source_year"] = 2025,
            ["kind"] = "PreRecommendation", ["title"] = "通知", ["registration_start_raw"] = null,
            ["registration_end_raw"] = null, ["event_start_raw"] = null, ["event_end_raw"] = null,
            ["official_url"] = "https://example.edu/official", ["application_url"] = null,
            ["source_url"] = "https://example.edu/source", ["raw_ref"] = new Dictionary<string, object?> { ["path"] = "raw/response.json", ["json_pointer"] = "/content/0" }
        };
        File.WriteAllText(Path.Combine(root, "records.jsonl"), JsonSerializer.Serialize(record) + "\n", Encoding.UTF8);
        var files = new[] { FileEntry(root, "records.jsonl", "records"), FileEntry(root, "raw/response.json", "raw") };
        var manifest = new Dictionary<string, object?>
        {
            ["schema_version"] = 1, ["source"] = "baoyan", ["adapter_version"] = "test",
            ["scope"] = new Dictionary<string, object?> { ["school"] = "示例大学", ["kind"] = "全部", ["year"] = null },
            ["fetched_at"] = "2026-09-05T00:00:00Z", ["complete"] = true,
            ["counts"] = new Dictionary<string, object?> { ["scanned"] = 1, ["expected"] = 1, ["selected"] = 1 },
            ["files"] = files
        };
        File.WriteAllText(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(manifest), Encoding.UTF8);
        var valid = CollectorManifestValidator.ReadAndValidate(root, new CollectorLimits(MaxRecords: 10, MaxBytes: 100_000));
        Check(valid.Counts.Selected == 1 && valid.Files.Count == 2, "collector manifest fixture was rejected");
        manifest["source"] = "other";
        File.WriteAllText(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(manifest), Encoding.UTF8);
        Expect("COLLECTOR_SOURCE_UNSUPPORTED", () => CollectorManifestValidator.ReadAndValidate(root, new CollectorLimits(MaxRecords: 10, MaxBytes: 100_000)));
        manifest["source"] = "baoyan";
        files[0]["sha256"] = new string('0', 64);
        File.WriteAllText(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(manifest), Encoding.UTF8);
        Expect("COLLECTOR_FILE_HASH_MISMATCH", () => CollectorManifestValidator.ReadAndValidate(root, new CollectorLimits(MaxRecords: 10, MaxBytes: 100_000)));
        var request = JsonSerializer.Serialize(new CollectorRequest("baoyan", new("示例大学", "全部"), new(10, 100_000, 30_000)));
        Check(request.Contains("\"max_records\":10") && request.Contains("\"timeout_ms\":30000"), "collector request did not use machine field names");
        Console.WriteLine("PASS: collector manifest hash, path, count and request contract validation");
        return Task.CompletedTask;
    }

    private static Dictionary<string, object?> FileEntry(string root, string relative, string kind)
    {
        var bytes = File.ReadAllBytes(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return new() { ["path"] = relative, ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), ["byte_length"] = bytes.Length, ["kind"] = kind };
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Expect(string code, Action action)
    {
        try { action(); throw new Exception("Expected " + code); }
        catch (InvalidOperationException error) when (error.Message == code) { }
    }
}
