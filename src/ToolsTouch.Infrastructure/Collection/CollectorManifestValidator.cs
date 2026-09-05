using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ToolsTouch.Application;

namespace ToolsTouch.Infrastructure.Collection;

public static class CollectorManifestValidator
{
    private static readonly HashSet<string> ManifestProperties = ["schema_version", "source", "adapter_version", "scope", "fetched_at", "complete", "counts", "files"];
    private static readonly HashSet<string> CountProperties = ["scanned", "expected", "selected"];
    private static readonly HashSet<string> FileProperties = ["path", "sha256", "byte_length", "kind"];
    private static readonly HashSet<string> RecordProperties = ["external_id", "school", "department", "source_year", "kind", "title",
        "registration_start_raw", "registration_end_raw", "event_start_raw", "event_end_raw", "official_url", "application_url", "source_url", "raw_ref"];

    public static CollectorManifest ReadAndValidate(string stagingDirectory, CollectorLimits limits)
    {
        var root = Path.GetFullPath(stagingDirectory);
        if (!Directory.Exists(root)) throw new InvalidOperationException("COLLECTOR_STAGING_MISSING");
        var manifestPath = SafePath(root, "manifest.json");
        if (!File.Exists(manifestPath)) throw new InvalidOperationException("COLLECTOR_MANIFEST_MISSING");
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath, Encoding.UTF8));
        var rootElement = document.RootElement;
        RequireObject(rootElement, ManifestProperties, "INVALID_MANIFEST_SCHEMA");
        var schemaVersion = RequiredInt(rootElement, "schema_version");
        if (schemaVersion != 1) throw new InvalidOperationException("COLLECTOR_SCHEMA_UNSUPPORTED");
        var source = RequiredString(rootElement, "source");
        if (source != "baoyan") throw new InvalidOperationException("COLLECTOR_SOURCE_UNSUPPORTED");
        var adapterVersion = RequiredString(rootElement, "adapter_version");
        var fetchedAt = RequiredDate(rootElement, "fetched_at");
        var complete = RequiredBoolean(rootElement, "complete");
        if (!complete) throw new InvalidOperationException("COLLECTOR_INCOMPLETE");
        var scope = ReadScope(rootElement.GetProperty("scope"));
        var counts = ReadCounts(rootElement.GetProperty("counts"));
        if (counts.Selected > limits.MaxRecords || counts.Scanned < counts.Selected) throw new InvalidOperationException("COLLECTOR_COUNT_INVALID");
        if (counts.Expected is { } expected && expected < counts.Scanned) throw new InvalidOperationException("COLLECTOR_COUNT_INVALID");

        var files = ReadFiles(root, rootElement.GetProperty("files"), limits.MaxBytes, out var recordsPath, out var rawPaths);
        var recordCount = ReadRecords(root, recordsPath, rawPaths, source, limits.MaxRecords);
        if (recordCount != counts.Selected) throw new InvalidOperationException("COLLECTOR_RECORD_COUNT_MISMATCH");
        return new(schemaVersion, source, adapterVersion, scope, fetchedAt, complete, counts, files);
    }

    private static IReadOnlyDictionary<string, object?> ReadScope(JsonElement scope)
    {
        if (scope.ValueKind != JsonValueKind.Object || scope.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal)
            is not { Count: 3 } keys || !keys.SetEquals(["school", "kind", "year"]))
            throw new InvalidOperationException("INVALID_COLLECTOR_SCOPE");
        var school = scope.GetProperty("school");
        var kind = scope.GetProperty("kind");
        var year = scope.GetProperty("year");
        if (school.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(school.GetString()) ||
            kind.ValueKind != JsonValueKind.String || kind.GetString() is not ("全部" or "夏令营" or "预推免") ||
            (year.ValueKind != JsonValueKind.Null && (!year.TryGetInt32(out var yearValue) || yearValue is < 1900 or > 2200)))
            throw new InvalidOperationException("INVALID_COLLECTOR_SCOPE");
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in scope.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) throw new InvalidOperationException("INVALID_COLLECTOR_SCOPE");
            result[property.Name] = property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.ToString();
        }
        return result;
    }

    private static CollectorCounts ReadCounts(JsonElement counts)
    {
        RequireObject(counts, CountProperties, "INVALID_COLLECTOR_COUNTS");
        var scanned = RequiredNonNegativeInt(counts, "scanned");
        int? expected = counts.GetProperty("expected").ValueKind == JsonValueKind.Null ? null : RequiredNonNegativeInt(counts, "expected");
        var selected = RequiredNonNegativeInt(counts, "selected");
        return new(scanned, expected, selected);
    }

    private static IReadOnlyList<CollectorFile> ReadFiles(string root, JsonElement files, long maxBytes, out string recordsPath, out HashSet<string> rawPaths)
    {
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0) throw new InvalidOperationException("INVALID_COLLECTOR_FILES");
        var result = new List<CollectorFile>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        rawPaths = new(StringComparer.Ordinal);
        recordsPath = "";
        long total = 0;
        foreach (var item in files.EnumerateArray())
        {
            RequireObject(item, FileProperties, "INVALID_COLLECTOR_FILE");
            var path = SafeRelativePath(item.GetProperty("path").GetString());
            if (!paths.Add(path)) throw new InvalidOperationException("DUPLICATE_COLLECTOR_PATH");
            var hash = RequiredString(item, "sha256");
            if (hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character))) throw new InvalidOperationException("INVALID_COLLECTOR_HASH");
            var length = RequiredNonNegativeLong(item, "byte_length");
            var kind = RequiredString(item, "kind");
            if (kind is not ("records" or "raw")) throw new InvalidOperationException("INVALID_COLLECTOR_FILE_KIND");
            if (kind == "records")
            {
                if (recordsPath != "" || path != "records.jsonl") throw new InvalidOperationException("INVALID_COLLECTOR_RECORDS_FILE");
                recordsPath = path;
            }
            else
            {
                if (!path.StartsWith("raw/", StringComparison.Ordinal)) throw new InvalidOperationException("INVALID_COLLECTOR_RAW_FILE");
                rawPaths.Add(path);
            }
            var file = SafePath(root, path);
            if (!File.Exists(file)) throw new InvalidOperationException("COLLECTOR_FILE_MISSING");
            var info = new FileInfo(file);
            if (info.Length != length) throw new InvalidOperationException("COLLECTOR_FILE_LENGTH_MISMATCH");
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("COLLECTOR_FILE_HASH_MISMATCH");
            total = checked(total + length);
            if (total > maxBytes) throw new InvalidOperationException("COLLECTOR_TOO_LARGE");
            result.Add(new(path, hash.ToLowerInvariant(), length, kind));
        }
        if (recordsPath == "") throw new InvalidOperationException("COLLECTOR_RECORDS_MISSING");
        return result;
    }

    private static int ReadRecords(string root, string recordsPath, HashSet<string> rawPaths, string source, int maxRecords)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        using var stream = new FileStream(SafePath(root, recordsPath), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        while (reader.ReadLine() is { } line)
        {
            if (Encoding.UTF8.GetByteCount(line) > 1_048_576 || string.IsNullOrWhiteSpace(line)) throw new InvalidOperationException("INVALID_COLLECTOR_RECORD_LINE");
            using var document = JsonDocument.Parse(line);
            var record = document.RootElement;
            RequireObject(record, RecordProperties, "INVALID_COLLECTOR_RECORD_SCHEMA");
            var externalId = RequiredString(record, "external_id");
            if (!identities.Add(externalId)) throw new InvalidOperationException("DUPLICATE_COLLECTOR_EXTERNAL_ID");
            _ = RequiredString(record, "school");
            _ = RequiredString(record, "kind");
            _ = RequiredString(record, "title");
            foreach (var property in new[] { "department", "registration_start_raw", "registration_end_raw", "event_start_raw", "event_end_raw", "official_url", "application_url", "source_url" })
                OptionalStringOrNull(record, property);
            OptionalYear(record, "source_year");
            var kind = record.GetProperty("kind").GetString();
            if (kind is not ("SummerCamp" or "PreRecommendation" or "PreRecommendation|SummerCamp"))
                throw new InvalidOperationException("INVALID_COLLECTOR_KIND");
            ValidateUrlOrNull(record, "official_url"); ValidateUrlOrNull(record, "application_url");
            if (record.GetProperty("source_url").ValueKind != JsonValueKind.String) throw new InvalidOperationException("INVALID_COLLECTOR_SOURCE_URL");
            ValidateUrl(record.GetProperty("source_url").GetString()!, "INVALID_COLLECTOR_SOURCE_URL");
            var rawRef = record.GetProperty("raw_ref");
            RequireObject(rawRef, ["path", "json_pointer"], "INVALID_COLLECTOR_RAW_REF", allowMissing: ["json_pointer"]);
            var rawPath = SafeRelativePath(rawRef.GetProperty("path").GetString());
            if (!rawPaths.Contains(rawPath)) throw new InvalidOperationException("INVALID_COLLECTOR_RAW_REF");
            if (rawRef.TryGetProperty("json_pointer", out var pointer) && (pointer.ValueKind != JsonValueKind.String || !pointer.GetString()!.StartsWith('/')))
                throw new InvalidOperationException("INVALID_COLLECTOR_RAW_REF");
            if (++count > maxRecords) throw new InvalidOperationException("COLLECTOR_RECORD_LIMIT");
        }
        return count;
    }

    private static void ValidateUrlOrNull(JsonElement value, string property)
    {
        var item = value.GetProperty(property);
        if (item.ValueKind == JsonValueKind.Null) return;
        if (item.ValueKind != JsonValueKind.String) throw new InvalidOperationException("INVALID_COLLECTOR_URL");
        ValidateUrl(item.GetString()!, "INVALID_COLLECTOR_URL");
    }

    private static void ValidateUrl(string value, string error)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(uri.Host))
            throw new InvalidOperationException(error);
    }

    private static void OptionalStringOrNull(JsonElement value, string property)
    {
        var item = value.GetProperty(property);
        if (item.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw new InvalidOperationException("INVALID_COLLECTOR_RECORD_FIELD");
    }

    private static void OptionalYear(JsonElement value, string property)
    {
        var item = value.GetProperty(property);
        if (item.ValueKind is JsonValueKind.Null) return;
        if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var year) || year is < 1900 or > 2200)
            throw new InvalidOperationException("INVALID_COLLECTOR_YEAR");
    }

    private static string SafeRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\\')) throw new InvalidOperationException("INVALID_COLLECTOR_PATH");
        var normalized = value.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(normalized)) throw new InvalidOperationException("INVALID_COLLECTOR_PATH");
        var parts = value.Split('/');
        if (parts.Any(part => part is "" or "." or "..")) throw new InvalidOperationException("INVALID_COLLECTOR_PATH");
        var canonical = string.Join('/', parts);
        if (canonical != value) throw new InvalidOperationException("INVALID_COLLECTOR_PATH");
        return canonical;
    }

    private static string SafePath(string root, string relative)
    {
        var safe = SafeRelativePath(relative);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, safe.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("INVALID_COLLECTOR_PATH");
        var current = fullRoot.TrimEnd(Path.DirectorySeparatorChar);
        foreach (var part in safe.Split('/'))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("COLLECTOR_SYMLINK_FORBIDDEN");
        }
        return full;
    }

    private static void RequireObject(JsonElement value, IEnumerable<string> properties, string error, IEnumerable<string>? allowMissing = null)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException(error);
        var allowed = properties.ToHashSet(StringComparer.Ordinal);
        var missingAllowed = allowMissing?.ToHashSet(StringComparer.Ordinal) ?? [];
        if (value.EnumerateObject().Any(property => !allowed.Contains(property.Name)) ||
            allowed.Except(value.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal).Any(property => !missingAllowed.Contains(property)))
            throw new InvalidOperationException(error);
    }

    private static string RequiredString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString())
            ? item.GetString()! : throw new InvalidOperationException("INVALID_COLLECTOR_FIELD");
    private static int RequiredInt(JsonElement value, string property) => value.TryGetProperty(property, out var item) && item.TryGetInt32(out var result) ? result : throw new InvalidOperationException("INVALID_COLLECTOR_FIELD");
    private static int RequiredNonNegativeInt(JsonElement value, string property) => RequiredInt(value, property) is var result && result >= 0 ? result : throw new InvalidOperationException("INVALID_COLLECTOR_COUNTS");
    private static long RequiredNonNegativeLong(JsonElement value, string property) => value.TryGetProperty(property, out var item) && item.TryGetInt64(out var result) && result >= 0 ? result : throw new InvalidOperationException("INVALID_COLLECTOR_LENGTH");
    private static bool RequiredBoolean(JsonElement value, string property) => value.TryGetProperty(property, out var item) && item.ValueKind is JsonValueKind.True or JsonValueKind.False ? item.GetBoolean() : throw new InvalidOperationException("INVALID_COLLECTOR_FIELD");
    private static DateTimeOffset RequiredDate(JsonElement value, string property) => value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(item.GetString(), out var result) ? result : throw new InvalidOperationException("INVALID_COLLECTOR_TIME");
}
