using System.Globalization;
using System.Text.Json;
using ToolsTouch.Application;

namespace ToolsTouch.Infrastructure.Tracking;

internal sealed record QueryParameter(string Name, object? Value);

internal sealed record CompiledSort(
    string FieldId,
    string Type,
    string Expression,
    bool Descending,
    bool NullsFirst);

internal sealed class CompiledRecordQuery
{
    public required string FromSql { get; init; }
    public string FilterSql { get; set; } = "1=1";
    public IReadOnlyList<CompiledSort> Sorts { get; set; } = [];
    public List<QueryParameter> Parameters { get; } = [];
    public int ParameterNumber { get; set; }

    public string Parameter(object? value, string prefix)
    {
        var name = "$" + prefix + ParameterNumber.ToString(CultureInfo.InvariantCulture);
        ParameterNumber++;
        Parameters.Add(new QueryParameter(name, value));
        return name;
    }
}

internal sealed class RecordQueryCompiler
{
    public const string RecordIdFieldId = "__record_id";
    private readonly string collectionId;
    private readonly IReadOnlyDictionary<string, FieldDefinitionRecord> fields;
    private readonly CompiledRecordQuery query;
    private int nodeCount;

    private RecordQueryCompiler(string collectionId, IReadOnlyList<FieldDefinitionRecord> definitions,
        string? filterJson, string? sortJson)
    {
        this.collectionId = Required(collectionId, nameof(collectionId));
        fields = definitions.Where(field => field.ArchivedAt is null)
            .ToDictionary(field => field.Id, StringComparer.OrdinalIgnoreCase);
        query = new CompiledRecordQuery
        {
            FromSql = SourceFor(collectionId),
            FilterSql = "1=1",
            Sorts = []
        };
        query.FilterSql = CompileFilter(filterJson);
        query.Sorts = CompileSort(sortJson);
    }

    public static CompiledRecordQuery Compile(string collectionId, IReadOnlyList<FieldDefinitionRecord> fields,
        string? filterJson, string? sortJson)
    {
        var compiler = new RecordQueryCompiler(collectionId, fields, filterJson, sortJson);
        return compiler.query;
    }

    private string CompileFilter(string? json)
    {
        var normalized = string.IsNullOrWhiteSpace(json) ? "{}" : json;
        try
        {
            using var document = JsonDocument.Parse(normalized);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.EnumerateObject().Any()
                ? CompileNode(document.RootElement, 1)
                : "1=1";
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException("FILTER_JSON_INVALID", error);
        }
    }

    private string CompileNode(JsonElement element, int depth)
    {
        if (depth > 5) throw new InvalidOperationException("FILTER_DEPTH_EXCEEDED");
        if (++nodeCount > 100) throw new InvalidOperationException("FILTER_NODE_LIMIT_EXCEEDED");
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("FILTER_NODE_INVALID");

        var kind = StringProperty(element, "kind") ?? StringProperty(element, "type");
        if (kind is null && TryGetProperty(element, "and", out var implicitAnd))
        {
            kind = "and";
            element = GroupElement("and", implicitAnd);
        }
        if (kind is null && TryGetProperty(element, "or", out var implicitOr))
        {
            kind = "or";
            element = GroupElement("or", implicitOr);
        }
        if (kind is not null && (kind.Equals("and", StringComparison.OrdinalIgnoreCase) || kind.Equals("or", StringComparison.OrdinalIgnoreCase)))
        {
            if (!TryGetProperty(element, "children", out var children) || children.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("FILTER_CHILDREN_REQUIRED");
            var compiled = children.EnumerateArray().Select(child => CompileNode(child, depth + 1)).ToArray();
            if (compiled.Length == 0) return kind.Equals("and", StringComparison.OrdinalIgnoreCase) ? "1=1" : "0=1";
            return "(" + string.Join(kind.Equals("and", StringComparison.OrdinalIgnoreCase) ? " AND " : " OR ", compiled) + ")";
        }

        var fieldName = StringProperty(element, "field_id") ?? StringProperty(element, "fieldId") ?? StringProperty(element, "field");
        var @operator = (StringProperty(element, "operator") ?? StringProperty(element, "op"))?.Trim().ToLowerInvariant();
        if (fieldName is null || @operator is null) throw new InvalidOperationException("FILTER_LEAF_INVALID");
        var field = ResolveField(fieldName);
        var expression = field.Type == "Relation" && field.StorageKind == "Custom" ? "" : Expression(field);

        if (@operator == "isempty" || @operator == "is_empty")
            return EmptyPredicate(field, expression);
        if (!TryGetProperty(element, "value", out var value)) throw new InvalidOperationException("FILTER_VALUE_REQUIRED");

        return @operator switch
        {
            "eq" or "equals" => Equality(field, expression, value),
            "contains" => Contains(field, expression, value),
            "gt" or "greaterthan" => Compare(field, expression, value, ">"),
            "gte" or "greaterthanorequal" => Compare(field, expression, value, ">="),
            "lt" or "lessthan" => Compare(field, expression, value, "<"),
            "lte" or "lessthanorequal" => Compare(field, expression, value, "<="),
            "range" => Range(field, expression, value),
            _ => throw new InvalidOperationException("FILTER_OPERATOR_UNSUPPORTED")
        };
    }

    private IReadOnlyList<CompiledSort> CompileSort(string? json)
    {
        var result = new List<CompiledSort>();
        var normalized = string.IsNullOrWhiteSpace(json) ? "[]" : json;
        try
        {
            using var document = JsonDocument.Parse(normalized);
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("SORT_JSON_INVALID");
            if (document.RootElement.GetArrayLength() > 10) throw new InvalidOperationException("SORT_LIMIT_EXCEEDED");
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("SORT_ITEM_INVALID");
                var fieldName = StringProperty(item, "field_id") ?? StringProperty(item, "fieldId") ?? StringProperty(item, "field");
                if (fieldName is null) throw new InvalidOperationException("SORT_FIELD_REQUIRED");
                var field = fieldName is RecordIdFieldId
                    ? null
                    : ResolveField(fieldName);
                var fieldId = field is null ? RecordIdFieldId : field.Id;
                if (result.Any(sort => string.Equals(sort.FieldId, fieldId, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("SORT_FIELD_DUPLICATE");
                var type = field?.Type ?? "Text";
                if (field?.Type == "Relation") throw new InvalidOperationException("SORT_RELATION_UNSUPPORTED");
                var direction = (StringProperty(item, "direction") ?? "asc").Trim().ToLowerInvariant();
                if (direction is not ("asc" or "desc")) throw new InvalidOperationException("SORT_DIRECTION_INVALID");
                var nulls = (StringProperty(item, "nulls") ?? "last").Trim().ToLowerInvariant();
                if (TryGetProperty(item, "nulls_first", out var nullsFirstValue) && nullsFirstValue.ValueKind == JsonValueKind.True) nulls = "first";
                if (nulls is not ("first" or "last")) throw new InvalidOperationException("SORT_NULLS_INVALID");
                result.Add(new(fieldId, type, field is null ? "r.Id" : Expression(field, forSort: true), direction == "desc", nulls == "first"));
            }
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException("SORT_JSON_INVALID", error);
        }

        if (!result.Any(sort => sort.FieldId == RecordIdFieldId)) result.Add(new(RecordIdFieldId, "Text", "r.Id", false, false));
        return result;
    }

    private string Equality(FieldDefinitionRecord field, string expression, JsonElement value)
    {
        if (field.Type == "Relation" && field.StorageKind == "Custom")
            return RelationPredicate(field, value);
        var parameter = query.Parameter(ReadValue(field, value), "filter");
        if (field.Type is "Text" or "Url") return $"({expression} IS NOT NULL AND {expression} = {parameter} COLLATE NOCASE)";
        if (field.Type is "Choice" or "MultiChoice" && field.StorageKind == "Custom")
            return $"EXISTS (SELECT 1 FROM json_each(COALESCE({expression},'[]')) WHERE value = {parameter})";
        return $"({expression} IS NOT NULL AND {expression} = {parameter})";
    }

    private string Contains(FieldDefinitionRecord field, string expression, JsonElement value)
    {
        if (field.Type == "Relation" && field.StorageKind == "Custom") return RelationPredicate(field, value);
        var parameter = query.Parameter(ReadString(value), "filter");
        if (field.Type is "Choice" or "MultiChoice" && field.StorageKind == "Custom")
            return $"EXISTS (SELECT 1 FROM json_each(COALESCE({expression},'[]')) WHERE value = {parameter})";
        if (field.Type is not ("Text" or "Url")) throw new InvalidOperationException("FILTER_OPERATOR_UNSUPPORTED");
        return $"({expression} IS NOT NULL AND instr(lower({expression}),lower({parameter})) > 0)";
    }

    private string Compare(FieldDefinitionRecord field, string expression, JsonElement value, string operation)
    {
        if (field.Type is not ("Number" or "DateTime")) throw new InvalidOperationException("FILTER_OPERATOR_UNSUPPORTED");
        var parameter = query.Parameter(ReadValue(field, value), "filter");
        return $"({expression} IS NOT NULL AND {expression} {operation} {parameter})";
    }

    private string Range(FieldDefinitionRecord field, string expression, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("FILTER_RANGE_INVALID");
        if (!TryGetProperty(value, "min", out var min) && !TryGetProperty(value, "max", out var max))
            throw new InvalidOperationException("FILTER_RANGE_EMPTY");
        if (field.Type is not ("Number" or "DateTime")) throw new InvalidOperationException("FILTER_OPERATOR_UNSUPPORTED");
        var predicates = new List<string>();
        if (TryGetProperty(value, "min", out min)) predicates.Add($"{expression} >= {query.Parameter(ReadValue(field, min), "filter")}");
        if (TryGetProperty(value, "max", out max)) predicates.Add($"{expression} <= {query.Parameter(ReadValue(field, max), "filter")}");
        return $"({expression} IS NOT NULL AND {string.Join(" AND ", predicates)})";
    }

    private string RelationPredicate(FieldDefinitionRecord field, JsonElement value)
    {
        var targets = value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(ReadString).ToArray()
            : [ReadString(value)];
        if (targets.Length == 0 || targets.Any(string.IsNullOrWhiteSpace)) throw new InvalidOperationException("FILTER_RELATION_TARGET_REQUIRED");
        var fieldParameter = query.Parameter(field.Id, "field");
        var targetParameters = targets.Select(target => query.Parameter(target, "filter")).ToArray();
        var predicate = string.Join(" OR ", targetParameters.Select(parameter => "rr.ToRecordId=" + parameter));
        return $"EXISTS (SELECT 1 FROM RecordRelation rr WHERE rr.FieldId={fieldParameter} AND rr.FromRecordId=r.Id AND ({predicate}))";
    }

    private string EmptyPredicate(FieldDefinitionRecord field, string expression)
    {
        if (field.Type == "Relation" && field.StorageKind == "Custom")
            return $"NOT EXISTS (SELECT 1 FROM RecordRelation rr WHERE rr.FieldId={query.Parameter(field.Id, "field")} AND rr.FromRecordId=r.Id)";
        return field.Type is "Choice" or "MultiChoice" && field.StorageKind == "Custom"
            ? $"({expression} IS NULL OR {expression} = '' OR {expression} = '[]')"
            : field.Type == "Boolean" ? $"{expression} IS NULL" : $"({expression} IS NULL OR {expression} = '')";
    }

    private FieldDefinitionRecord ResolveField(string name)
    {
        var field = fields.Values.FirstOrDefault(candidate => string.Equals(candidate.Id, name, StringComparison.OrdinalIgnoreCase)) ??
            fields.Values.FirstOrDefault(candidate => string.Equals(candidate.Key, name, StringComparison.OrdinalIgnoreCase));
        return field ?? throw new InvalidOperationException("FILTER_FIELD_NOT_ALLOWED");
    }

    private string Expression(FieldDefinitionRecord field, bool forSort = false)
    {
        if (field.StorageKind == "System") return SystemExpression(field);
        if (forSort && field.Type == "Relation") throw new InvalidOperationException("SORT_RELATION_UNSUPPORTED");
        var valueColumn = field.Type switch
        {
            "Text" or "Url" => "TextValue",
            "Number" => "NumberValue",
            "DateTime" => "DateValue",
            "Boolean" => "BoolValue",
            "Choice" or "MultiChoice" => "JsonValue",
            "Relation" => throw new InvalidOperationException("RELATION_VALUE_IS_NOT_SCALAR"),
            _ => throw new InvalidOperationException("FIELD_TYPE_UNSUPPORTED")
        };
        var fieldParameter = query.Parameter(field.Id, "field");
        return $"(SELECT fv.{valueColumn} FROM FieldValue fv WHERE fv.RecordId=r.Id AND fv.FieldId={fieldParameter})";
    }

    private static string SystemExpression(FieldDefinitionRecord field) => field.Id switch
    {
        "school-name" => "e.CanonicalName",
        "school-short-name" => "e.ShortName",
        "school-city" => "e.City",
        "department-name" => "e.CanonicalName",
        "department-school" => "('system-schools:' || e.SchoolId)",
        "department-kind" => "e.Kind",
        "program-name" => "e.Name",
        "program-degree" => "e.DegreeType",
        "program-department" => "('system-departments:' || e.DepartmentId)",
        "round-title" => "e.Title",
        "round-cycle-year" => "e.CycleYear",
        "round-application-url" => "e.ApplicationUrl",
        "professor-name" => "e.Name",
        "professor-institution" => "e.Institution",
        "professor-email" => "e.Email",
        "application-stage" => "e.Stage",
        "application-priority" => "e.Priority",
        "application-url" => "e.ApplicationUrl",
        "application-next-step" => "e.NextStep",
        "application-note" => "e.OwnerNote",
        "application-submitted-at" => "e.SubmittedAt",
        "outreach-subject" => "e.Subject",
        "outreach-state" => "e.State",
        "outreach-recipient" => "e.Recipient",
        "task-kind" => "e.Kind",
        "task-state" => "e.State",
        _ => throw new InvalidOperationException("SYSTEM_FIELD_NOT_ALLOWED")
    };

    public static string SourceFor(string collectionId) => collectionId switch
    {
        "system-schools" => "JOIN School e ON e.Id=r.EntityId",
        "system-departments" => "JOIN Department e ON e.Id=r.EntityId",
        "system-programs" => "JOIN AdmissionProgram e ON e.Id=r.EntityId",
        "system-rounds" => "JOIN AdmissionRound e ON e.Id=r.EntityId",
        "system-professors" => "JOIN Professor e ON e.Id=r.EntityId",
        "system-applications" => "JOIN ApplicationCase e ON e.Id=r.EntityId",
        "system-outreach" => "JOIN Outreach e ON e.Id=r.EntityId",
        "system-tasks" => "JOIN AgentRun e ON e.Id=r.EntityId",
        _ => ""
    };

    private object ReadValue(FieldDefinitionRecord field, JsonElement value) => field.Type switch
    {
        "Text" or "Url" => ReadString(value),
        "Number" => ReadNumber(value),
        "DateTime" => ReadDate(value),
        "Boolean" => ReadBoolean(value),
        "Choice" or "MultiChoice" => field.StorageKind == "Custom" ? ReadString(value) : ReadString(value),
        "Relation" => ReadString(value),
        _ => throw new InvalidOperationException("FIELD_TYPE_UNSUPPORTED")
    };

    private static double ReadNumber(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
        throw new InvalidOperationException("FILTER_NUMBER_INVALID");
    }

    private static string ReadDate(JsonElement value)
    {
        var raw = ReadString(value);
        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            throw new InvalidOperationException("FILTER_DATE_INVALID");
        return parsed.ToUniversalTime().ToString("O");
    }

    private static int ReadBoolean(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean() ? 1 : 0;
        if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed)) return parsed ? 1 : 0;
        throw new InvalidOperationException("FILTER_BOOLEAN_INVALID");
    }

    private static string ReadString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw new InvalidOperationException("FILTER_TEXT_INVALID");
        return value.GetString()!.Trim();
    }

    private static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A value is required.", name) : value.Trim();

    private static string? StringProperty(JsonElement element, string name) => TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value)) return true;
        var camel = name switch
        {
            "field_id" => "fieldId",
            "nulls_first" => "nullsFirst",
            _ => name
        };
        return camel != name && element.TryGetProperty(camel, out value);
    }

    private static JsonElement GroupElement(string kind, JsonElement children)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { kind, children }));
        return document.RootElement.Clone();
    }
}
