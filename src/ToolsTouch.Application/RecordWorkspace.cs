namespace ToolsTouch.Application;

public static class RecordFieldTypes
{
    public static readonly IReadOnlyList<string> All = ["Text", "Number", "DateTime", "Boolean", "Url", "Choice", "MultiChoice", "Relation"];
}

public static class RecordEditPolicies
{
    public static readonly IReadOnlyList<string> All = ["Editable", "OverrideWithReason", "SystemManaged"];
}

public sealed record CollectionRecord(
    string Id,
    string Name,
    string Kind,
    string? SystemEntityKind,
    string? Description,
    DateTimeOffset? ArchivedAt,
    int Revision);

public sealed record RecordRefRecord(
    string Id,
    string CollectionId,
    string? EntityKind,
    string? EntityId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ArchivedAt,
    int Revision);

public sealed record FieldDefinitionRecord(
    string Id,
    string CollectionId,
    string Key,
    string DisplayName,
    string Type,
    string StorageKind,
    string? SystemBinding,
    string? OptionsJson,
    bool Required,
    string EditPolicy,
    DateTimeOffset? ArchivedAt,
    int Revision);

public sealed record FieldChoiceRecord(
    string Id,
    string FieldId,
    string Label,
    string? ColorToken,
    int SortOrder,
    DateTimeOffset? ArchivedAt);

public sealed record FieldValueRecord(
    string RecordId,
    string FieldId,
    string? TextValue,
    decimal? NumberValue,
    DateTimeOffset? DateValue,
    bool? BoolValue,
    string? JsonValue,
    int Revision);

public sealed record RecordRelationRecord(string FieldId, string FromRecordId, string ToRecordId, int SortOrder);

public sealed record ViewDefinitionRecord(
    string Id,
    string CollectionId,
    string Name,
    string ViewType,
    string FilterAstJson,
    string SortJson,
    string GroupJson,
    string ColumnsJson,
    int Revision);

public sealed record RecordChangeRecord(
    string Id,
    string RecordId,
    string? FieldId,
    string CommandId,
    string ActorKind,
    string? BeforeJson,
    string? AfterJson,
    DateTimeOffset OccurredAt,
    string? UndoOf);

public sealed record CustomCollectionCreateRequest(
    string Name,
    string? Description = null,
    string? Id = null);

public sealed record CustomFieldCreateRequest(
    string CollectionId,
    string Key,
    string DisplayName,
    string Type,
    bool Required = false,
    string? OptionsJson = null,
    string? Id = null);

public sealed record CustomRecordCreateRequest(
    string CollectionId,
    string? Id = null);

public sealed record FieldChoiceCreateRequest(
    string FieldId,
    string Label,
    string? ColorToken = null,
    int SortOrder = 0,
    string? Id = null);

public sealed record TypedRecordValue(
    string Type,
    string? TextValue = null,
    decimal? NumberValue = null,
    DateTimeOffset? DateValue = null,
    bool? BoolValue = null,
    IReadOnlyList<string>? ChoiceIds = null,
    IReadOnlyList<string>? RelationRecordIds = null)
{
    public bool IsEmpty => TextValue is null && NumberValue is null && DateValue is null && BoolValue is null &&
        (ChoiceIds is null || ChoiceIds.Count == 0) && (RelationRecordIds is null || RelationRecordIds.Count == 0);
}

public sealed record UpdateCellCommand(
    string RecordId,
    string FieldId,
    int ExpectedRecordRevision,
    int ExpectedFieldDefinitionRevision,
    TypedRecordValue Value,
    string CommandId,
    string? Reason = null);

public sealed record RecordCellResult(
    string RecordId,
    string FieldId,
    int RecordRevision,
    TypedRecordValue? Value,
    string ChangeId);

public sealed record BulkEditRequest(IReadOnlyList<UpdateCellCommand> Updates, string CommandId);
public sealed record BulkEditResult(int Applied, IReadOnlyList<RecordCellResult> Results);

public sealed record ImportFieldMapping(string SourceColumn, string FieldId, bool StableKey = false);

public sealed record ImportRequest(
    string CollectionId,
    string SourcePath,
    string CommandId,
    IReadOnlyList<ImportFieldMapping> Mappings,
    string Format = "csv",
    string DuplicateStrategy = "Append",
    string? SheetName = null,
    int MaxRows = 100_000);

public sealed record ImportRowError(int RowNumber, string Code, string? SourceColumn, string? FieldId, string Message);

public sealed record ImportPreviewRow(
    int RowNumber,
    IReadOnlyDictionary<string, string?> Values,
    string? RecordId,
    bool CreateRecord,
    IReadOnlyList<ImportRowError> Errors)
{
    public bool Accepted => Errors.Count == 0;
}

public sealed record ImportPreviewResult(
    string BatchId,
    string CollectionId,
    string SourceKind,
    string Format,
    string DuplicateStrategy,
    string SourceHash,
    int ScannedCount,
    int AcceptedCount,
    int RejectedCount,
    IReadOnlyList<string> Columns,
    IReadOnlyList<ImportPreviewRow> Rows,
    IReadOnlyList<ImportRowError> Errors);

public sealed record ImportCommitResult(
    string BatchId,
    string State,
    int ScannedCount,
    int AcceptedCount,
    int RejectedCount,
    int CreatedCount,
    int UpdatedCount,
    IReadOnlyList<ImportRowError> Errors);

public sealed record ImportedWriteCell(
    string FieldId,
    int ExpectedFieldDefinitionRevision,
    TypedRecordValue Value,
    string CommandId);

public sealed record ImportedWriteRow(
    string RecordId,
    bool CreateRecord,
    int ExpectedRecordRevision,
    IReadOnlyList<ImportedWriteCell> Cells);

public sealed record ImportedWriteSummary(int CreatedCount, int UpdatedCount, int CellCount);

public sealed record SystemCollectionDefinition(
    string Id,
    string Name,
    string EntityKind,
    string Description,
    IReadOnlyList<SystemFieldDefinition> Fields);

public sealed record SystemFieldDefinition(
    string Id,
    string Key,
    string DisplayName,
    string Type,
    string Binding,
    string EditPolicy,
    bool Required);

public interface IRecordWorkspaceCatalog
{
    IReadOnlyList<CollectionRecord> EnsureSystemCatalog();
    IReadOnlyList<CollectionRecord> Collections();
    IReadOnlyList<FieldDefinitionRecord> Fields(string collectionId);
    IReadOnlyList<RecordRefRecord> Records(string collectionId);
    IReadOnlyList<FieldChoiceRecord> Choices(string fieldId);
}

public interface IRecordWorkspaceService
{
    CollectionRecord CreateCustomCollection(CustomCollectionCreateRequest request);
    FieldDefinitionRecord CreateCustomField(CustomFieldCreateRequest request);
    FieldChoiceRecord CreateChoice(FieldChoiceCreateRequest request);
    RecordRefRecord CreateCustomRecord(CustomRecordCreateRequest request);
    RecordCellResult UpdateCell(UpdateCellCommand command);
    BulkEditResult BulkEdit(BulkEditRequest request);
    RecordCellResult Undo(string changeId, string commandId, int expectedRecordRevision, string? reason = null);
    ImportedWriteSummary ApplyImportedRows(string collectionId, IReadOnlyList<ImportedWriteRow> rows);
    IReadOnlyList<FieldValueRecord> Values(string recordId);
    IReadOnlyList<RecordRelationRecord> Relations(string recordId, string? fieldId = null);
}

public interface IImportPreviewService
{
    ImportPreviewResult Preview(ImportRequest request, CancellationToken cancellationToken = default);
    ImportCommitResult Commit(string batchId, CancellationToken cancellationToken = default);
    ImportCommitResult Cancel(string batchId);
}

public interface IFieldSchemaService
{
    CollectionRecord CreateCustomCollection(CustomCollectionCreateRequest request);
    FieldDefinitionRecord CreateCustomField(CustomFieldCreateRequest request);
    FieldChoiceRecord CreateChoice(FieldChoiceCreateRequest request);
}
