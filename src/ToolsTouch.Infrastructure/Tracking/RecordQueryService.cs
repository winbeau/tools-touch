using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ToolsTouch.Application;
using ToolsTouch.Core;

namespace ToolsTouch.Infrastructure.Tracking;

public sealed class RecordQueryService(LocalDatabase database) : IRecordQueryService
{
    private readonly SystemCollectionAdapter catalog = new(database);
    private readonly ViewService views = new(database);

    public RecordQueryPage Query(RecordQueryRequest request)
    {
        var collectionId = Required(request.CollectionId, nameof(request.CollectionId));
        var collection = catalog.Collections().FirstOrDefault(item => item.Id == collectionId && item.ArchivedAt is null)
            ?? throw new KeyNotFoundException("COLLECTION_NOT_FOUND");
        var view = request.ViewId is null ? null : views.Get(request.ViewId);
        if (view is not null && view.CollectionId != collection.Id) throw new InvalidOperationException("VIEW_COLLECTION_MISMATCH");
        var fields = catalog.Fields(collection.Id).Where(field => field.ArchivedAt is null).ToArray();
        var filter = request.FilterAstJson ?? view?.FilterAstJson ?? "{}";
        var sort = request.SortJson ?? view?.SortJson ?? "[]";
        var compiled = RecordQueryCompiler.Compile(collection.Id, fields, filter, sort);
        var cursor = DecodeCursor(request.After, compiled.Sorts);

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var dataRevision = Convert.ToInt64(Scalar(connection, transaction, "SELECT DataRevision FROM WorkspaceMeta WHERE Id=1"));
        var after = cursor is null ? "" : " AND " + BuildAfterPredicate(compiled, cursor);
        var selectSorts = string.Join(",", compiled.Sorts.Select((item, index) => $"{item.Expression} AS __sort{index.ToString(CultureInfo.InvariantCulture)}"));
        var order = string.Join(",", compiled.Sorts.Select(item =>
            $"CASE WHEN {item.Expression} IS NULL THEN {(item.NullsFirst ? 0 : 1)} ELSE {(item.NullsFirst ? 1 : 0)} END ASC," +
            $"{item.Expression}" + (item.Type is "Text" or "Url" ? " COLLATE NOCASE" : "") + $" {(item.Descending ? "DESC" : "ASC")}"));
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT r.Id,r.CollectionId,r.EntityKind,r.EntityId,r.CreatedAt,r.ArchivedAt,r.Revision,{selectSorts}
            FROM RecordRef r {compiled.FromSql}
            WHERE r.CollectionId=$collection AND r.ArchivedAt IS NULL AND ({compiled.FilterSql}){after}
            ORDER BY {order}
            LIMIT $limit
            """;
        foreach (var parameter in compiled.Parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$collection", collection.Id);
        command.Parameters.AddWithValue("$limit", request.EffectiveLimit + 1);
        using var reader = command.ExecuteReader();
        var rawRows = new List<RawRecord>();
        while (reader.Read())
        {
            var reference = new RecordRefRecord(reader.GetString(0), reader.GetString(1), NullableString(reader, 2), NullableString(reader, 3),
                DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture), NullableDate(reader, 5), reader.GetInt32(6));
            var keys = new string?[compiled.Sorts.Count];
            for (var index = 0; index < keys.Length; index++) keys[index] = NullableSortKey(reader, 7 + index);
            rawRows.Add(new(reference, keys));
        }
        reader.Close();
        var hasNext = rawRows.Count > request.EffectiveLimit;
        if (hasNext) rawRows.RemoveAt(rawRows.Count - 1);
        var rows = rawRows.Select(raw => new RecordQueryRow(raw.Reference, ReadValues(connection, transaction, raw.Reference, fields))).ToArray();
        transaction.Commit();
        var next = hasNext && rawRows.Count > 0 ? EncodeCursor(new CursorPayload(compiled.Sorts.Select(item => item.FieldId).ToArray(), rawRows[^1].SortKeys)) : null;
        return new RecordQueryPage(rows, next, dataRevision, request.AsOf?.ToUniversalTime() ?? DateTimeOffset.UtcNow);
    }

    private static string BuildAfterPredicate(CompiledRecordQuery query, CursorPayload cursor)
    {
        var equalities = new List<string>();
        var branches = new List<string>();
        for (var index = 0; index < query.Sorts.Count; index++)
        {
            var sort = query.Sorts[index];
            var key = cursor.Keys[index];
            var keyParameter = query.Parameter(ParseCursorKey(sort.Type, key), "cursor");
            var nullRank = key is null ? (sort.NullsFirst ? 0 : 1) : (sort.NullsFirst ? 1 : 0);
            var rank = $"CASE WHEN {sort.Expression} IS NULL THEN {(sort.NullsFirst ? 0 : 1)} ELSE {(sort.NullsFirst ? 1 : 0)} END";
            var comparisonExpression = sort.Type is "Text" or "Url" ? sort.Expression + " COLLATE NOCASE" : sort.Expression;
            var equal = $"({rank}={nullRank} AND (({sort.Expression} IS NULL AND {keyParameter} IS NULL) OR ({sort.Expression} IS NOT NULL AND {keyParameter} IS NOT NULL AND {comparisonExpression}={keyParameter})))";
            var comparison = sort.Descending ? "<" : ">";
            var after = $"({rank}>{nullRank} OR ({rank}={nullRank} AND {sort.Expression} IS NOT NULL AND {keyParameter} IS NOT NULL AND {comparisonExpression}{comparison}{keyParameter}))";
            branches.Add("(" + string.Join(" AND ", equalities.Append(after)) + ")");
            equalities.Add(equal);
        }
        return "(" + string.Join(" OR ", branches) + ")";
    }

    private static IReadOnlyDictionary<string, TypedRecordValue?> ReadValues(SqliteConnection connection, SqliteTransaction transaction,
        RecordRefRecord reference, IReadOnlyList<FieldDefinitionRecord> fields)
    {
        var values = fields.ToDictionary(field => field.Id, _ => (TypedRecordValue?)null, StringComparer.OrdinalIgnoreCase);
        var types = fields.ToDictionary(field => field.Id, field => field, StringComparer.OrdinalIgnoreCase);
        using (var command = LocalDatabase.Command(connection, "SELECT FieldId,TextValue,NumberValue,DateValue,BoolValue,JsonValue FROM FieldValue WHERE RecordId=$record", ("$record", reference.Id)))
        {
            command.Transaction = transaction;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var fieldId = reader.GetString(0);
                if (!types.TryGetValue(fieldId, out var field)) continue;
                values[fieldId] = ReadCustomValue(reader, field);
            }
        }
        using (var command = LocalDatabase.Command(connection, "SELECT FieldId,ToRecordId FROM RecordRelation WHERE FromRecordId=$record ORDER BY FieldId,SortOrder,ToRecordId", ("$record", reference.Id)))
        {
            command.Transaction = transaction;
            using var reader = command.ExecuteReader();
            var relations = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                var fieldId = reader.GetString(0);
                if (!types.TryGetValue(fieldId, out var field) || field.Type != "Relation") continue;
                if (!relations.TryGetValue(fieldId, out var targets)) relations[fieldId] = targets = [];
                targets.Add(reader.GetString(1));
            }
            foreach (var relation in relations) values[relation.Key] = new TypedRecordValue(types[relation.Key].Type, RelationRecordIds: relation.Value);
        }
        if (reference.EntityId is not null)
        {
            foreach (var field in fields.Where(field => field.StorageKind == "System"))
                values[field.Id] = ReadSystemValue(connection, transaction, reference, field);
        }
        return values;
    }

    private static TypedRecordValue? ReadCustomValue(SqliteDataReader reader, FieldDefinitionRecord field)
    {
        return field.Type switch
        {
            "Text" or "Url" => reader.IsDBNull(1) ? null : new TypedRecordValue(field.Type, TextValue: reader.GetString(1)),
            "Number" => reader.IsDBNull(2) ? null : new TypedRecordValue(field.Type, NumberValue: Convert.ToDecimal(reader.GetDouble(2), CultureInfo.InvariantCulture)),
            "DateTime" => reader.IsDBNull(3) ? null : new TypedRecordValue(field.Type, DateValue: DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture)),
            "Boolean" => reader.IsDBNull(4) ? null : new TypedRecordValue(field.Type, BoolValue: reader.GetInt64(4) != 0),
            "Choice" or "MultiChoice" => ReadChoices(reader, field),
            _ => null
        };
    }

    private static TypedRecordValue? ReadChoices(SqliteDataReader reader, FieldDefinitionRecord field)
    {
        if (reader.IsDBNull(5)) return null;
        try
        {
            using var document = JsonDocument.Parse(reader.GetString(5));
            var choices = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
                : [];
            return choices.Length == 0 ? null : new TypedRecordValue(field.Type, ChoiceIds: choices);
        }
        catch (JsonException) { return null; }
    }

    private static TypedRecordValue? ReadSystemValue(SqliteConnection connection, SqliteTransaction transaction,
        RecordRefRecord reference, FieldDefinitionRecord field)
    {
        var sql = SystemValueSql(field.Id);
        using var command = LocalDatabase.Command(connection, sql, ("$id", reference.EntityId));
        command.Transaction = transaction;
        var raw = command.ExecuteScalar();
        if (raw is null or DBNull) return null;
        return field.Type switch
        {
            "Text" or "Url" => new TypedRecordValue(field.Type, TextValue: Convert.ToString(raw, CultureInfo.InvariantCulture)),
            "Number" => new TypedRecordValue(field.Type, NumberValue: Convert.ToDecimal(raw, CultureInfo.InvariantCulture)),
            "DateTime" => DateTimeOffset.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
                ? new TypedRecordValue(field.Type, DateValue: date) : null,
            "Boolean" => new TypedRecordValue(field.Type, BoolValue: Convert.ToInt64(raw, CultureInfo.InvariantCulture) != 0),
            "Choice" or "MultiChoice" => new TypedRecordValue(field.Type, ChoiceIds: [Convert.ToString(raw, CultureInfo.InvariantCulture)!]),
            "Relation" => new TypedRecordValue(field.Type, RelationRecordIds: [SystemRelationTarget(field.Id, Convert.ToString(raw, CultureInfo.InvariantCulture)!)]),
            _ => null
        };
    }

    private static string SystemValueSql(string fieldId) => fieldId switch
    {
        "school-name" => "SELECT CanonicalName FROM School WHERE Id=$id",
        "school-short-name" => "SELECT ShortName FROM School WHERE Id=$id",
        "school-city" => "SELECT City FROM School WHERE Id=$id",
        "department-name" => "SELECT CanonicalName FROM Department WHERE Id=$id",
        "department-school" => "SELECT SchoolId FROM Department WHERE Id=$id",
        "department-kind" => "SELECT Kind FROM Department WHERE Id=$id",
        "program-name" => "SELECT Name FROM AdmissionProgram WHERE Id=$id",
        "program-degree" => "SELECT DegreeType FROM AdmissionProgram WHERE Id=$id",
        "program-department" => "SELECT DepartmentId FROM AdmissionProgram WHERE Id=$id",
        "round-title" => "SELECT Title FROM AdmissionRound WHERE Id=$id",
        "round-cycle-year" => "SELECT CycleYear FROM AdmissionRound WHERE Id=$id",
        "round-application-url" => "SELECT ApplicationUrl FROM AdmissionRound WHERE Id=$id",
        "professor-name" => "SELECT Name FROM Professor WHERE Id=$id",
        "professor-institution" => "SELECT Institution FROM Professor WHERE Id=$id",
        "professor-email" => "SELECT Email FROM Professor WHERE Id=$id",
        "application-stage" => "SELECT Stage FROM ApplicationCase WHERE Id=$id",
        "application-priority" => "SELECT Priority FROM ApplicationCase WHERE Id=$id",
        "application-url" => "SELECT ApplicationUrl FROM ApplicationCase WHERE Id=$id",
        "application-next-step" => "SELECT NextStep FROM ApplicationCase WHERE Id=$id",
        "application-note" => "SELECT OwnerNote FROM ApplicationCase WHERE Id=$id",
        "application-submitted-at" => "SELECT SubmittedAt FROM ApplicationCase WHERE Id=$id",
        "outreach-subject" => "SELECT Subject FROM Outreach WHERE Id=$id",
        "outreach-state" => "SELECT State FROM Outreach WHERE Id=$id",
        "outreach-recipient" => "SELECT Recipient FROM Outreach WHERE Id=$id",
        "task-kind" => "SELECT Kind FROM AgentRun WHERE Id=$id",
        "task-state" => "SELECT State FROM AgentRun WHERE Id=$id",
        _ => throw new InvalidOperationException("SYSTEM_FIELD_NOT_ALLOWED")
    };

    private static string SystemRelationTarget(string fieldId, string entityId) => fieldId switch
    {
        "department-school" => "system-schools:" + entityId,
        "program-department" => "system-departments:" + entityId,
        _ => entityId
    };

    private static object? ParseCursorKey(string type, string? value)
    {
        if (value is null) return null;
        return type switch
        {
            "Number" => double.Parse(value, CultureInfo.InvariantCulture),
            "Boolean" => int.Parse(value, CultureInfo.InvariantCulture),
            _ => value
        };
    }

    private static CursorPayload? DecodeCursor(string? encoded, IReadOnlyList<CompiledSort> sorts)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<CursorPayload>(Convert.FromBase64String(encoded))
                ?? throw new InvalidOperationException("RECORD_CURSOR_INVALID");
            if (payload.FieldIds is null || payload.Keys is null || payload.FieldIds.Length != sorts.Count || payload.Keys.Length != sorts.Count ||
                !payload.FieldIds.SequenceEqual(sorts.Select(sort => sort.FieldId), StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("RECORD_CURSOR_INVALID");
            return payload;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            throw new InvalidOperationException("RECORD_CURSOR_INVALID", error);
        }
    }

    private static string EncodeCursor(CursorPayload payload) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(payload));
    private static object? Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = LocalDatabase.Command(connection, sql); command.Transaction = transaction; return command.ExecuteScalar();
    }

    private static string? NullableString(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static DateTimeOffset? NullableDate(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : DateTimeOffset.Parse(reader.GetString(index), CultureInfo.InvariantCulture);
    private static string? NullableSortKey(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture);
    private static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A value is required.", name) : value.Trim();
    private sealed record RawRecord(RecordRefRecord Reference, string?[] SortKeys);
    private sealed record CursorPayload(string[] FieldIds, string?[] Keys);
}
