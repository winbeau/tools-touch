using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ToolsTouch.Application;
using ToolsTouch.Core;

namespace ToolsTouch.Infrastructure.Tracking;

public sealed class RecordWorkspaceService(LocalDatabase database) : IRecordWorkspaceService
{
    private static string Now => DateTimeOffset.UtcNow.ToString("O");

    public CollectionRecord CreateCustomCollection(CustomCollectionCreateRequest request)
    {
        var name = Required(request.Name, nameof(request.Name));
        var description = Optional(request.Description);
        var id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : Required(request.Id, nameof(request.Id));
        var now = Now;
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var insert = LocalDatabase.Command(connection, """
            INSERT INTO Collection(Id,Name,Kind,SystemEntityKind,Description,Revision,CreatedAt,UpdatedAt)
            VALUES($id,$name,'Custom',NULL,$description,1,$now,$now)
            """, ("$id", id), ("$name", name), ("$description", description), ("$now", now));
        insert.Transaction = transaction;
        insert.ExecuteNonQuery();
        IncrementRevision(connection, transaction);
        transaction.Commit();
        return Collections(database).Single(item => item.Id == id);
    }

    public FieldDefinitionRecord CreateCustomField(CustomFieldCreateRequest request)
    {
        var collectionId = Required(request.CollectionId, nameof(request.CollectionId));
        var key = Required(request.Key, nameof(request.Key));
        var displayName = Required(request.DisplayName, nameof(request.DisplayName));
        var type = ValidateType(request.Type);
        var options = ValidateOptions(request.OptionsJson);
        var id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : Required(request.Id, nameof(request.Id));
        var now = Now;
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        EnsureCustomCollection(connection, transaction, collectionId);
        using var insert = LocalDatabase.Command(connection, """
            INSERT INTO FieldDefinition(Id,CollectionId,Key,DisplayName,Type,StorageKind,SystemBinding,OptionsJson,Required,EditPolicy,Revision,CreatedAt,UpdatedAt)
            VALUES($id,$collection,$key,$display,$type,'Custom',NULL,$options,$required,'Editable',1,$now,$now)
            """, ("$id", id), ("$collection", collectionId), ("$key", key), ("$display", displayName), ("$type", type),
            ("$options", options), ("$required", request.Required ? 1 : 0), ("$now", now));
        insert.Transaction = transaction;
        insert.ExecuteNonQuery();
        IncrementRevision(connection, transaction);
        transaction.Commit();
        return Fields(database, collectionId).Single(item => item.Id == id);
    }

    public FieldChoiceRecord CreateChoice(FieldChoiceCreateRequest request)
    {
        var fieldId = Required(request.FieldId, nameof(request.FieldId));
        var label = Required(request.Label, nameof(request.Label));
        var id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : Required(request.Id, nameof(request.Id));
        var now = Now;
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var field = ReadField(connection, transaction, fieldId);
        if (field.Type is not ("Choice" or "MultiChoice")) throw new InvalidOperationException("CHOICE_FIELD_REQUIRED");
        using var insert = LocalDatabase.Command(connection, "INSERT INTO FieldChoice(Id,FieldId,Label,ColorToken,SortOrder) VALUES($id,$field,$label,$color,$sort)",
            ("$id", id), ("$field", fieldId), ("$label", label), ("$color", Optional(request.ColorToken)), ("$sort", request.SortOrder));
        insert.Transaction = transaction;
        insert.ExecuteNonQuery();
        IncrementRevision(connection, transaction);
        transaction.Commit();
        return ReadChoice(id);
    }

    public RecordRefRecord CreateCustomRecord(CustomRecordCreateRequest request)
    {
        var collectionId = Required(request.CollectionId, nameof(request.CollectionId));
        var id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : Required(request.Id, nameof(request.Id));
        var now = Now;
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        EnsureCustomCollection(connection, transaction, collectionId);
        using var insert = LocalDatabase.Command(connection, "INSERT INTO RecordRef(Id,CollectionId,CreatedAt,Revision) VALUES($id,$collection,$now,1)",
            ("$id", id), ("$collection", collectionId), ("$now", now));
        insert.Transaction = transaction;
        insert.ExecuteNonQuery();
        IncrementRevision(connection, transaction);
        transaction.Commit();
        return ReadRecord(id);
    }

    public RecordCellResult UpdateCell(UpdateCellCommand command)
    {
        ValidateCommand(command);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var result = ApplyUpdate(connection, transaction, command, null);
        transaction.Commit();
        return result;
    }

    public BulkEditResult BulkEdit(BulkEditRequest request)
    {
        if (request.Updates is null || request.Updates.Count == 0) throw new ArgumentException("At least one cell is required.", nameof(request));
        if (request.Updates.Count > 1000) throw new ArgumentOutOfRangeException(nameof(request), "A bulk edit batch cannot exceed 1000 cells.");
        var batchCommand = Required(request.CommandId, nameof(request.CommandId));
        var updates = request.Updates.Select((item, index) =>
        {
            ValidateCommand(item);
            return string.IsNullOrWhiteSpace(item.CommandId) ? item with { CommandId = batchCommand + ":" + index.ToString(CultureInfo.InvariantCulture) } : item;
        }).ToArray();
        if (updates.GroupBy(item => item.RecordId, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidOperationException("BULK_EDIT_ONE_CELL_PER_RECORD");

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var results = new List<RecordCellResult>(updates.Length);
        foreach (var update in updates) results.Add(ApplyUpdate(connection, transaction, update, null));
        transaction.Commit();
        return new BulkEditResult(results.Count, results);
    }

    public ImportedWriteSummary ApplyImportedRows(string collectionId, IReadOnlyList<ImportedWriteRow> rows)
    {
        collectionId = Required(collectionId, nameof(collectionId));
        if (rows is null || rows.Count == 0) throw new ArgumentException("At least one import row is required.", nameof(rows));
        if (rows.Count > 100_000) throw new ArgumentOutOfRangeException(nameof(rows), "An import batch cannot exceed 100000 rows.");

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        EnsureCustomCollection(connection, transaction, collectionId);
        var created = 0;
        var updated = 0;
        var cells = 0;
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.RecordId) || row.Cells is null || row.Cells.Count == 0)
                throw new InvalidOperationException("IMPORT_ROW_EMPTY");
            if (row.Cells.GroupBy(cell => cell.FieldId, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
                throw new InvalidOperationException("IMPORT_FIELD_DUPLICATE");

            var existed = RecordExists(connection, transaction, row.RecordId);
            if (row.CreateRecord)
            {
                using var insert = LocalDatabase.Command(connection, "INSERT OR IGNORE INTO RecordRef(Id,CollectionId,CreatedAt,Revision) VALUES($id,$collection,$now,1)",
                    ("$id", row.RecordId), ("$collection", collectionId), ("$now", Now));
                insert.Transaction = transaction;
                if (insert.ExecuteNonQuery() == 1) { created++; existed = false; }
                else if (!existed) throw new InvalidOperationException("IMPORT_RECORD_ID_CONFLICT");
            }
            else
            {
                if (!existed) throw new KeyNotFoundException("IMPORT_RECORD_NOT_FOUND");
                updated++;
            }

            var record = ReadRecordInfo(connection, transaction, row.RecordId);
            if (record.CollectionId != collectionId) throw new InvalidOperationException("IMPORT_RECORD_COLLECTION_MISMATCH");
            if (!row.CreateRecord && record.Revision != row.ExpectedRecordRevision)
                throw new InvalidOperationException("IMPORT_RECORD_CHANGED");
            var revision = record.Revision;
            foreach (var cell in row.Cells)
            {
                ValidateCommand(new UpdateCellCommand(row.RecordId, cell.FieldId, revision, cell.ExpectedFieldDefinitionRevision, cell.Value, cell.CommandId));
                var result = ApplyUpdate(connection, transaction,
                    new UpdateCellCommand(row.RecordId, cell.FieldId, revision, cell.ExpectedFieldDefinitionRevision, cell.Value, cell.CommandId), null);
                revision = result.RecordRevision;
                cells++;
            }
        }
        transaction.Commit();
        return new ImportedWriteSummary(created, updated, cells);
    }

    public RecordCellResult Undo(string changeId, string commandId, int expectedRecordRevision, string? reason = null)
    {
        var change = Required(changeId, nameof(changeId));
        var command = Required(commandId, nameof(commandId));
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        string recordId;
        string fieldId;
        string? beforeJson;
        using (var lookup = LocalDatabase.Command(connection, "SELECT RecordId,FieldId,BeforeJson FROM RecordChange WHERE Id=$id", ("$id", change)))
        {
            lookup.Transaction = transaction;
            using var reader = lookup.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(1)) throw new KeyNotFoundException("RECORD_CHANGE_NOT_FOUND");
            recordId = reader.GetString(0); fieldId = reader.GetString(1); beforeJson = reader.IsDBNull(2) ? null : reader.GetString(2);
        }
        var field = ReadField(connection, transaction, fieldId);
        var value = beforeJson is null ? new TypedRecordValue(field.Type) : JsonSerializer.Deserialize<TypedRecordValue>(beforeJson) ?? new TypedRecordValue(field.Type);
        var result = ApplyUpdate(connection, transaction, new UpdateCellCommand(recordId, fieldId, expectedRecordRevision, field.Revision, value, command, reason), change);
        transaction.Commit();
        return result;
    }

    public IReadOnlyList<FieldValueRecord> Values(string recordId)
    {
        Required(recordId, nameof(recordId));
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "SELECT RecordId,FieldId,TextValue,NumberValue,DateValue,BoolValue,JsonValue,Revision FROM FieldValue WHERE RecordId=$record ORDER BY FieldId", ("$record", recordId));
        using var reader = command.ExecuteReader();
        var result = new List<FieldValueRecord>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), NullableString(reader, 2), NullableDecimal(reader, 3), NullableDate(reader, 4), NullableBool(reader, 5), NullableString(reader, 6), reader.GetInt32(7)));
        return result;
    }

    public IReadOnlyList<RecordRelationRecord> Relations(string recordId, string? fieldId = null)
    {
        Required(recordId, nameof(recordId));
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "SELECT FieldId,FromRecordId,ToRecordId,SortOrder FROM RecordRelation WHERE FromRecordId=$record AND ($field IS NULL OR FieldId=$field) ORDER BY FieldId,SortOrder,ToRecordId", ("$record", recordId), ("$field", fieldId));
        using var reader = command.ExecuteReader();
        var result = new List<RecordRelationRecord>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        return result;
    }

    private static RecordCellResult ApplyUpdate(SqliteConnection connection, SqliteTransaction transaction, UpdateCellCommand command, string? undoOf)
    {
        using (var existing = LocalDatabase.Command(connection, "SELECT Id FROM RecordChange WHERE RecordId=$record AND FieldId=$field AND CommandId=$command", ("$record", command.RecordId), ("$field", command.FieldId), ("$command", command.CommandId)))
        {
            existing.Transaction = transaction;
            if (existing.ExecuteScalar() is string existingChange)
                return new(command.RecordId, command.FieldId, ReadRecordRevision(connection, transaction, command.RecordId), ReadValue(connection, transaction, command.RecordId, command.FieldId, ReadField(connection, transaction, command.FieldId).Type), existingChange);
        }

        var field = ReadField(connection, transaction, command.FieldId);
        if (field.EditPolicy == "SystemManaged" || field.StorageKind == "System") throw new InvalidOperationException("SYSTEM_FIELD_DOMAIN_COMMAND_REQUIRED");
        if (field.EditPolicy == "OverrideWithReason" && string.IsNullOrWhiteSpace(command.Reason)) throw new InvalidOperationException("FIELD_CORRECTION_REASON_REQUIRED");
        var record = ReadRecordInfo(connection, transaction, command.RecordId);
        if (record.CollectionId != field.CollectionId) throw new InvalidOperationException("FIELD_RECORD_COLLECTION_MISMATCH");
        if (field.Revision != command.ExpectedFieldDefinitionRevision) throw new InvalidOperationException("FIELD_DEFINITION_CHANGED");
        var value = NormalizeAndValidateValue(connection, transaction, field, command.Value);
        var before = ReadValue(connection, transaction, command.RecordId, command.FieldId, field.Type);
        if (record.Revision != command.ExpectedRecordRevision) throw new InvalidOperationException("RECORD_CHANGED");

        using (var revision = LocalDatabase.Command(connection, "UPDATE RecordRef SET Revision=Revision+1 WHERE Id=$id AND Revision=$revision", ("$id", command.RecordId), ("$revision", record.Revision)))
        {
            revision.Transaction = transaction;
            if (revision.ExecuteNonQuery() != 1) throw new InvalidOperationException("RECORD_CHANGED");
        }
        using (var deleteValue = LocalDatabase.Command(connection, "DELETE FROM FieldValue WHERE RecordId=$record AND FieldId=$field", ("$record", command.RecordId), ("$field", command.FieldId)))
        { deleteValue.Transaction = transaction; deleteValue.ExecuteNonQuery(); }
        using (var deleteRelations = LocalDatabase.Command(connection, "DELETE FROM RecordRelation WHERE FieldId=$field AND FromRecordId=$record", ("$field", command.FieldId), ("$record", command.RecordId)))
        { deleteRelations.Transaction = transaction; deleteRelations.ExecuteNonQuery(); }
        InsertValue(connection, transaction, command.RecordId, command.FieldId, value);
        if (field.Type == "Relation" && value.RelationRecordIds is not null)
        {
            for (var index = 0; index < value.RelationRecordIds.Count; index++)
            {
                using var relation = LocalDatabase.Command(connection, "INSERT INTO RecordRelation(FieldId,FromRecordId,ToRecordId,SortOrder) VALUES($field,$from,$to,$sort)",
                    ("$field", command.FieldId), ("$from", command.RecordId), ("$to", value.RelationRecordIds[index]), ("$sort", index));
                relation.Transaction = transaction;
                relation.ExecuteNonQuery();
            }
        }
        var after = ReadValue(connection, transaction, command.RecordId, command.FieldId, field.Type);
        var changeId = Guid.NewGuid().ToString("N");
        using (var change = LocalDatabase.Command(connection, """
            INSERT INTO RecordChange(Id,RecordId,FieldId,CommandId,ActorKind,BeforeJson,AfterJson,OccurredAt,UndoOf)
            VALUES($id,$record,$field,$command,'User',$before,$after,$now,$undo)
            """, ("$id", changeId), ("$record", command.RecordId), ("$field", command.FieldId), ("$command", command.CommandId),
            ("$before", before is null ? null : JsonSerializer.Serialize(before)), ("$after", after is null ? null : JsonSerializer.Serialize(after)),
            ("$now", Now), ("$undo", undoOf)))
        { change.Transaction = transaction; change.ExecuteNonQuery(); }
        IncrementRevision(connection, transaction);
        return new(command.RecordId, command.FieldId, record.Revision + 1, after, changeId);
    }

    private static bool RecordExists(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = LocalDatabase.Command(connection, "SELECT COUNT(*) FROM RecordRef WHERE Id=$id", ("$id", id));
        command.Transaction = transaction;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static TypedRecordValue? ReadValue(SqliteConnection connection, SqliteTransaction transaction, string recordId, string fieldId, string type)
    {
        if (type == "Relation")
        {
            using var relation = LocalDatabase.Command(connection, "SELECT ToRecordId FROM RecordRelation WHERE FieldId=$field AND FromRecordId=$record ORDER BY SortOrder,ToRecordId", ("$field", fieldId), ("$record", recordId));
            relation.Transaction = transaction;
            using var reader = relation.ExecuteReader();
            var ids = new List<string>(); while (reader.Read()) ids.Add(reader.GetString(0));
            return ids.Count == 0 ? null : new TypedRecordValue(type, RelationRecordIds: ids);
        }
        using var command = LocalDatabase.Command(connection, "SELECT TextValue,NumberValue,DateValue,BoolValue,JsonValue FROM FieldValue WHERE RecordId=$record AND FieldId=$field", ("$record", recordId), ("$field", fieldId));
        command.Transaction = transaction;
        using var valueReader = command.ExecuteReader();
        if (!valueReader.Read()) return null;
        return type switch
        {
            "Text" or "Url" => new(type, TextValue: NullableString(valueReader, 0)),
            "Number" => new(type, NumberValue: NullableDecimal(valueReader, 1)),
            "DateTime" => new(type, DateValue: NullableDate(valueReader, 2)),
            "Boolean" => new(type, BoolValue: NullableBool(valueReader, 3)),
            "Choice" or "MultiChoice" => new(type, ChoiceIds: JsonSerializer.Deserialize<string[]>(NullableString(valueReader, 4) ?? "[]") ?? []),
            _ => throw new InvalidOperationException("UNSUPPORTED_FIELD_TYPE")
        };
    }

    private static TypedRecordValue NormalizeAndValidateValue(SqliteConnection connection, SqliteTransaction transaction, FieldInfo field, TypedRecordValue value)
    {
        if (value.Type != field.Type) throw new InvalidOperationException("FIELD_VALUE_TYPE_MISMATCH");
        if (value.IsEmpty) return new(field.Type);
        switch (field.Type)
        {
            case "Text" or "Url" when value.TextValue is not null && value.NumberValue is null && value.DateValue is null && value.BoolValue is null && value.ChoiceIds is null && value.RelationRecordIds is null:
                if (field.Type == "Url" && (!Uri.TryCreate(value.TextValue, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))) throw new ArgumentException("Only an HTTP(S) URL is allowed.", nameof(value));
                return new(field.Type, TextValue: value.TextValue);
            case "Number" when value.NumberValue is not null && OtherSlotsEmpty(value, number: true):
                return new(field.Type, NumberValue: value.NumberValue);
            case "DateTime" when value.DateValue is not null && OtherSlotsEmpty(value, date: true):
                return new(field.Type, DateValue: value.DateValue.Value.ToUniversalTime());
            case "Boolean" when value.BoolValue is not null && OtherSlotsEmpty(value, boolean: true):
                return new(field.Type, BoolValue: value.BoolValue);
            case "Choice" or "MultiChoice" when value.ChoiceIds is not null && OtherSlotsEmpty(value, choices: true):
                var choices = value.ChoiceIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                if (field.Type == "Choice" && choices.Length != 1) throw new InvalidOperationException("SINGLE_CHOICE_REQUIRED");
                EnsureChoices(connection, transaction, field.Id, choices);
                return new(field.Type, ChoiceIds: choices);
            case "Relation" when value.RelationRecordIds is not null && OtherSlotsEmpty(value, relations: true):
                var relations = value.RelationRecordIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                EnsureRelationTargets(connection, transaction, field, relations);
                return new(field.Type, RelationRecordIds: relations);
            default: throw new InvalidOperationException("FIELD_VALUE_TYPE_MISMATCH");
        }
    }

    private static bool OtherSlotsEmpty(TypedRecordValue value, bool number = false, bool date = false, bool boolean = false, bool choices = false, bool relations = false) =>
        (number || value.NumberValue is null) && (date || value.DateValue is null) && (boolean || value.BoolValue is null) &&
        (choices || value.ChoiceIds is null) && (relations || value.RelationRecordIds is null) && (value.TextValue is null);

    private static void EnsureChoices(SqliteConnection connection, SqliteTransaction transaction, string fieldId, IReadOnlyList<string> choiceIds)
    {
        if (choiceIds.Count == 0) throw new InvalidOperationException("CHOICE_REQUIRED");
        using var command = LocalDatabase.Command(connection, "SELECT COUNT(*) FROM FieldChoice WHERE FieldId=$field AND ArchivedAt IS NULL AND Id IN (" + string.Join(',', choiceIds.Select((_, i) => "$choice" + i)) + ")", ("$field", fieldId));
        for (var i = 0; i < choiceIds.Count; i++) command.Parameters.AddWithValue("$choice" + i, choiceIds[i]);
        command.Transaction = transaction;
        if (Convert.ToInt32(command.ExecuteScalar()) != choiceIds.Count) throw new InvalidOperationException("CHOICE_NOT_FOUND");
    }

    private static void EnsureRelationTargets(SqliteConnection connection, SqliteTransaction transaction, FieldInfo field, IReadOnlyList<string> targets)
    {
        if (targets.Count == 0) throw new InvalidOperationException("RELATION_TARGET_REQUIRED");
        string? targetCollection = null;
        if (!string.IsNullOrWhiteSpace(field.OptionsJson))
        {
            using var options = JsonDocument.Parse(field.OptionsJson);
            if (options.RootElement.TryGetProperty("target_collection_id", out var target)) targetCollection = target.GetString();
        }
        using var command = LocalDatabase.Command(connection, "SELECT COUNT(*) FROM RecordRef WHERE Id IN (" + string.Join(',', targets.Select((_, i) => "$target" + i)) + ")" + (targetCollection is null ? "" : " AND CollectionId=$collection"), ("$collection", targetCollection));
        for (var i = 0; i < targets.Count; i++) command.Parameters.AddWithValue("$target" + i, targets[i]);
        command.Transaction = transaction;
        if (Convert.ToInt32(command.ExecuteScalar()) != targets.Count) throw new InvalidOperationException("RELATION_TARGET_NOT_FOUND");
    }

    private static void InsertValue(SqliteConnection connection, SqliteTransaction transaction, string recordId, string fieldId, TypedRecordValue value)
    {
        if (value.IsEmpty || value.Type == "Relation") return;
        using var insert = LocalDatabase.Command(connection, """
            INSERT INTO FieldValue(RecordId,FieldId,TextValue,NumberValue,DateValue,BoolValue,JsonValue,Revision)
            VALUES($record,$field,$text,$number,$date,$bool,$json,1)
            """, ("$record", recordId), ("$field", fieldId), ("$text", value.TextValue), ("$number", value.NumberValue is null ? null : Convert.ToDouble(value.NumberValue.Value, CultureInfo.InvariantCulture)),
            ("$date", value.DateValue?.ToUniversalTime().ToString("O")), ("$bool", value.BoolValue is null ? null : value.BoolValue.Value ? 1 : 0),
            ("$json", value.ChoiceIds is null ? null : JsonSerializer.Serialize(value.ChoiceIds)));
        insert.Transaction = transaction;
        insert.ExecuteNonQuery();
    }

    private static void ValidateCommand(UpdateCellCommand command)
    {
        Required(command.RecordId, nameof(command.RecordId)); Required(command.FieldId, nameof(command.FieldId)); Required(command.CommandId, nameof(command.CommandId));
        if (command.ExpectedRecordRevision <= 0 || command.ExpectedFieldDefinitionRevision <= 0) throw new ArgumentOutOfRangeException(nameof(command));
        if (command.Value is null) throw new ArgumentNullException(nameof(command.Value));
    }

    private static string ValidateType(string value)
    {
        value = Required(value, nameof(value));
        return RecordFieldTypes.All.Contains(value, StringComparer.Ordinal) ? value : throw new ArgumentException("Unknown record field type.", nameof(value));
    }

    private static string? ValidateOptions(string? value)
    {
        value = Optional(value);
        if (value is not null) JsonDocument.Parse(value).Dispose();
        return value;
    }

    private static void EnsureCustomCollection(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = LocalDatabase.Command(connection, "SELECT Kind FROM Collection WHERE Id=$id AND ArchivedAt IS NULL", ("$id", id));
        command.Transaction = transaction;
        if (command.ExecuteScalar() as string != "Custom") throw new InvalidOperationException("CUSTOM_COLLECTION_REQUIRED");
    }

    private static FieldInfo ReadField(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = LocalDatabase.Command(connection, "SELECT Id,CollectionId,Type,StorageKind,SystemBinding,OptionsJson,EditPolicy,Revision FROM FieldDefinition WHERE Id=$id AND ArchivedAt IS NULL", ("$id", id));
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("FIELD_NOT_FOUND");
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), NullableString(reader, 4), NullableString(reader, 5), reader.GetString(6), reader.GetInt32(7));
    }

    private static RecordInfo ReadRecordInfo(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = LocalDatabase.Command(connection, "SELECT CollectionId,Revision FROM RecordRef WHERE Id=$id AND ArchivedAt IS NULL", ("$id", id));
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("RECORD_NOT_FOUND");
        return new(reader.GetString(0), reader.GetInt32(1));
    }

    private static int ReadRecordRevision(SqliteConnection connection, SqliteTransaction transaction, string id) => ReadRecordInfo(connection, transaction, id).Revision;

    private RecordRefRecord ReadRecord(string id)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "SELECT Id,CollectionId,EntityKind,EntityId,CreatedAt,ArchivedAt,Revision FROM RecordRef WHERE Id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("RECORD_NOT_FOUND");
        return new(reader.GetString(0), reader.GetString(1), NullableString(reader, 2), NullableString(reader, 3), Date(reader, 4), NullableDate(reader, 5), reader.GetInt32(6));
    }

    private FieldChoiceRecord ReadChoice(string id)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "SELECT Id,FieldId,Label,ColorToken,SortOrder,ArchivedAt FROM FieldChoice WHERE Id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("CHOICE_NOT_FOUND");
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), NullableString(reader, 3), reader.GetInt32(4), NullableDate(reader, 5));
    }

    private static IReadOnlyList<CollectionRecord> Collections(LocalDatabase database)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "SELECT Id,Name,Kind,SystemEntityKind,Description,ArchivedAt,Revision FROM Collection ORDER BY Kind,Name COLLATE NOCASE,Id");
        using var reader = command.ExecuteReader();
        var result = new List<CollectionRecord>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), NullableString(reader, 3), NullableString(reader, 4), NullableDate(reader, 5), reader.GetInt32(6)));
        return result;
    }

    private static IReadOnlyList<FieldDefinitionRecord> Fields(LocalDatabase database, string collectionId)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "SELECT Id,CollectionId,Key,DisplayName,Type,StorageKind,SystemBinding,OptionsJson,Required,EditPolicy,ArchivedAt,Revision FROM FieldDefinition WHERE CollectionId=$collection AND Id NOT NULL", ("$collection", collectionId));
        using var reader = command.ExecuteReader();
        var result = new List<FieldDefinitionRecord>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), NullableString(reader, 6), NullableString(reader, 7), reader.GetInt64(8) != 0, reader.GetString(9), NullableDate(reader, 10), reader.GetInt32(11)));
        return result;
    }

    private static void IncrementRevision(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = LocalDatabase.Command(connection, "UPDATE WorkspaceMeta SET DataRevision=DataRevision+1 WHERE Id=1");
        command.Transaction = transaction;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("WORKSPACE_META_MISSING");
    }

    private static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A value is required.", name) : value.Trim();
    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? NullableString(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static decimal? NullableDecimal(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : Convert.ToDecimal(reader.GetDouble(index), CultureInfo.InvariantCulture);
    private static bool? NullableBool(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt64(index) != 0;
    private static DateTimeOffset Date(SqliteDataReader reader, int index) => DateTimeOffset.Parse(reader.GetString(index));
    private static DateTimeOffset? NullableDate(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : Date(reader, index);

    private sealed record FieldInfo(string Id, string CollectionId, string Type, string StorageKind, string? SystemBinding, string? OptionsJson, string EditPolicy, int Revision);
    private sealed record RecordInfo(string CollectionId, int Revision);
}
