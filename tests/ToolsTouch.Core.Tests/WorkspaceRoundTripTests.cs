using System.IO.Compression;
using System.Text.Json;
using ToolsTouch.Application;
using ToolsTouch.Core;
using ToolsTouch.Infrastructure.Persistence;
using ToolsTouch.Infrastructure.Tracking;

static class WorkspaceRoundTripTests
{
    public static Task RunAsync(LocalDatabase database, string directory)
    {
        var catalog = new SystemCollectionAdapter(database);
        catalog.EnsureSystemCatalog();
        var records = new RecordWorkspaceService(database);
        var collection = records.CreateCustomCollection(new CustomCollectionCreateRequest("往返测试表", Id: "roundtrip-collection"));
        var note = records.CreateCustomField(new CustomFieldCreateRequest(collection.Id, "note", "备注", "Text", Id: "roundtrip-note"));
        var targetCollection = records.CreateCustomCollection(new CustomCollectionCreateRequest("往返目标表", Id: "roundtrip-targets"));
        var relation = records.CreateCustomField(new CustomFieldCreateRequest(collection.Id, "target", "关联", "Relation",
            OptionsJson: "{\"target_collection_id\":\"roundtrip-targets\"}", Id: "roundtrip-relation"));
        var target = records.CreateCustomRecord(new CustomRecordCreateRequest(targetCollection.Id, "roundtrip-target-1"));
        var row = records.CreateCustomRecord(new CustomRecordCreateRequest(collection.Id, "roundtrip-record-1"));
        row = Write(records, row, note, new TypedRecordValue("Text", TextValue: "=formula text"), "roundtrip-note-write");
        row = Write(records, row, relation, new TypedRecordValue("Relation", RelationRecordIds: [target.Id]), "roundtrip-relation-write");
        var view = new ViewService(database).Save(new ViewSaveRequest(collection.Id, "往返视图",
            FilterAstJson: FilterBuilder.Condition(note.Id, "contains", "formula"), SortJson: "[]", ColumnsJson: "[\"roundtrip-note\"]"));
        var artifactStore = new ArtifactStore(database.ArtifactDirectory);
        var artifact = artifactStore.Stage("roundtrip", "附件内容"u8.ToArray(), "text/plain", ".txt");
        using (var connection = database.Open())
        using (var transaction = connection.BeginTransaction())
        {
            ArtifactStore.Insert(connection, transaction, artifact);
            transaction.Commit();
        }

        var exporter = new WorkspaceExportService(database);
        var bundlePath = Path.Combine(directory, "roundtrip-workspace.zip");
        var exported = exporter.Export(new WorkspaceExportRequest("WorkspaceAll", "business_bundle", bundlePath, IncludeAttachments: true));
        Check(exported.OutputPath == bundlePath && File.Exists(bundlePath) && exported.TableCount > 10 && exported.RowCount > 10, "full export creates a business bundle from all tables");
        var workbookPath = Path.Combine(directory, "roundtrip-workspace.xlsx");
        var workbook = new WorkspaceExportService(database, "uv").Export(new WorkspaceExportRequest("WorkspaceAll", "xlsx", workbookPath,
            IncludeAttachments: false, MaxRowsPerSheet: 100));
        Check(workbook.OutputPath == workbookPath && File.Exists(workbookPath) && new FileInfo(workbookPath).Length > 0, "C# XLSX bridge publishes the Python workbook atomically");
        using (var archive = ZipFile.OpenRead(bundlePath))
        {
            var manifestEntry = archive.GetEntry("manifest.json") ?? throw new Exception("manifest missing");
            using var reader = new StreamReader(manifestEntry.Open());
            using var manifest = JsonDocument.Parse(reader.ReadToEnd());
            Check(manifest.RootElement.GetProperty("full_backup").GetBoolean() &&
                manifest.RootElement.GetProperty("tables").EnumerateArray().Any(item => item.GetProperty("name").GetString() == "RecordChange") &&
                manifest.RootElement.GetProperty("files").EnumerateArray().Any(item => item.GetProperty("path").GetString() == "artifacts/roundtrip/" + artifact.ContentHash + ".txt"),
                "bundle manifest records full backup, history and attachment hashes");
        }

        var restoreDirectory = Path.Combine(directory, "restored-workspace");
        var restored = exporter.RestoreToNewWorkspace(bundlePath, restoreDirectory);
        var restoredDatabase = new LocalDatabase(restored.DatabasePath);
        var restoredCatalog = new SystemCollectionAdapter(restoredDatabase);
        var restoredQuery = new RecordQueryService(restoredDatabase).Query(new RecordQueryRequest(collection.Id, ViewId: view.Id, Limit: 10));
        Check(restored.WorkspaceId == new OrganizationRepository(database).Get().WorkspaceId &&
            restoredQuery.Items.Single().Record.Id == row.Id && restoredQuery.Items.Single().Value(note.Id)?.TextValue == "=formula text" &&
            restoredQuery.Items.Single().Value(relation.Id)?.RelationRecordIds?.Single() == target.Id,
            "restored workspace retains IDs, custom typed values, relations and saved views");
        var restoredCollection = restoredCatalog.Collections().Any(item => item.Id == collection.Id);
        var restoredArtifactPath = Path.Combine(restoredDatabase.ArtifactDirectory, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Check(restoredCollection && File.Exists(restoredArtifactPath),
            $"restored workspace contains system metadata and referenced attachments (collection={restoredCollection}, artifact={restoredArtifactPath})");
        Check(Count(restoredDatabase, "SELECT COUNT(*) FROM RecordChange WHERE RecordId='roundtrip-record-1'") >= 2, "record history is restored");
        Console.WriteLine("PASS: full JSONL bundle, manifest hashes, custom values, relations, saved views, history and isolated restore");
        return Task.CompletedTask;
    }

    private static RecordRefRecord Write(RecordWorkspaceService service, RecordRefRecord record, FieldDefinitionRecord field, TypedRecordValue value, string commandId)
    {
        var result = service.UpdateCell(new UpdateCellCommand(record.Id, field.Id, record.Revision, field.Revision, value, commandId));
        return record with { Revision = result.RecordRevision };
    }

    private static int Count(LocalDatabase database, string sql)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
