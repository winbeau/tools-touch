using System.Text.Json.Serialization;

namespace ToolsTouch.Application;

public sealed record CollectorScope(
    [property: JsonPropertyName("school")] string School,
    [property: JsonPropertyName("kind")] string Kind = "全部",
    [property: JsonPropertyName("year")] int? Year = null);

public sealed record CollectorLimits(
    [property: JsonPropertyName("max_records")] int MaxRecords = 100_000,
    [property: JsonPropertyName("max_bytes")] long MaxBytes = 100 * 1024 * 1024,
    [property: JsonPropertyName("timeout_ms")] int TimeoutMilliseconds = 300_000);

public sealed record CollectorRequest(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("scope")] CollectorScope Scope,
    [property: JsonPropertyName("limits")] CollectorLimits Limits);

public sealed record CollectorFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("byte_length")] long ByteLength,
    [property: JsonPropertyName("kind")] string Kind);

public sealed record CollectorCounts(
    [property: JsonPropertyName("scanned")] int Scanned,
    [property: JsonPropertyName("expected")] int? Expected,
    [property: JsonPropertyName("selected")] int Selected);

public sealed record CollectorManifest(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("adapter_version")] string AdapterVersion,
    [property: JsonPropertyName("scope")] IReadOnlyDictionary<string, object?> Scope,
    [property: JsonPropertyName("fetched_at")] DateTimeOffset FetchedAt,
    [property: JsonPropertyName("complete")] bool Complete,
    [property: JsonPropertyName("counts")] CollectorCounts Counts,
    [property: JsonPropertyName("files")] IReadOnlyList<CollectorFile> Files);

public sealed record CollectorResult(string StagingDirectory, CollectorManifest Manifest);

public interface ICollectorBridge
{
    Task<CollectorResult> ExportBaoyanAsync(CollectorRequest request, CancellationToken cancellationToken = default);
}
