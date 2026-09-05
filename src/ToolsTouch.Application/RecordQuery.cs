using System.Text.Json;

namespace ToolsTouch.Application;

public sealed record RecordQueryRequest(
    string CollectionId,
    string? ViewId = null,
    string? FilterAstJson = null,
    string? SortJson = null,
    int Limit = 50,
    string? After = null,
    DateTimeOffset? AsOf = null)
{
    public int EffectiveLimit => Math.Clamp(Limit, 1, 200);
}

public sealed record RecordQueryRow(
    RecordRefRecord Record,
    IReadOnlyDictionary<string, TypedRecordValue?> Values)
{
    public TypedRecordValue? Value(string fieldId) => Values.TryGetValue(fieldId, out var value) ? value : null;
}

public sealed record RecordQueryPage(
    IReadOnlyList<RecordQueryRow> Items,
    string? NextCursor,
    long DataRevision,
    DateTimeOffset AsOf);

public sealed record ViewSaveRequest(
    string CollectionId,
    string Name,
    string ViewType = "Grid",
    string? FilterAstJson = null,
    string? SortJson = null,
    string? GroupJson = null,
    string? ColumnsJson = null,
    string? Id = null,
    int? ExpectedRevision = null);

public sealed record ViewCopyRequest(string SourceViewId, string Name, string? Id = null);

public interface IRecordQueryService
{
    RecordQueryPage Query(RecordQueryRequest request);
}

public interface IViewService
{
    IReadOnlyList<ViewDefinitionRecord> List(string collectionId);
    ViewDefinitionRecord Get(string viewId);
    ViewDefinitionRecord Save(ViewSaveRequest request);
    ViewDefinitionRecord Copy(ViewCopyRequest request);
}

public static class FilterBuilder
{
    public static string Empty() => "{}";

    public static string Condition(string fieldId, string @operator, object? value = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldId);
        ArgumentException.ThrowIfNullOrWhiteSpace(@operator);
        var node = new Dictionary<string, object?>
        {
            ["field_id"] = fieldId,
            ["operator"] = @operator
        };
        if (value is not null) node["value"] = value;
        return JsonSerializer.Serialize(node);
    }

    public static string Group(string kind, params string[] children)
    {
        if (!string.Equals(kind, "and", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(kind, "or", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Filter groups must be and or or.", nameof(kind));
        var parsed = children.Select(child => JsonDocument.Parse(child)).Select(document =>
        {
            using (document) return document.RootElement.Clone();
        }).ToArray();
        return JsonSerializer.Serialize(new { kind = kind.ToLowerInvariant(), children = parsed });
    }
}
