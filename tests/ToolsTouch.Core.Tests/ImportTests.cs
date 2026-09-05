using ToolsTouch.Application;
using ToolsTouch.Core;
using ToolsTouch.Infrastructure.Tracking;

static class ImportTests
{
    public static Task RunAsync(LocalDatabase database, string directory)
    {
        var catalog = new SystemCollectionAdapter(database);
        catalog.EnsureSystemCatalog();
        var records = new RecordWorkspaceService(database);
        var importer = new ImportPreviewService(database);
        var collection = records.CreateCustomCollection(new CustomCollectionCreateRequest("导入测试表", Id: "import-collection"));
        var key = records.CreateCustomField(new CustomFieldCreateRequest(collection.Id, "key", "稳定键", "Text", Required: true, Id: "import-key"));
        var note = records.CreateCustomField(new CustomFieldCreateRequest(collection.Id, "note", "备注", "Text", Id: "import-note"));
        var score = records.CreateCustomField(new CustomFieldCreateRequest(collection.Id, "score", "分数", "Number", Id: "import-score"));
        var choice = records.CreateCustomField(new CustomFieldCreateRequest(collection.Id, "kind", "类型", "Choice", Id: "import-kind"));
        var choiceA = records.CreateChoice(new FieldChoiceCreateRequest(choice.Id, "甲", Id: "import-kind-a"));
        var targetCollection = records.CreateCustomCollection(new CustomCollectionCreateRequest("导入目标表", Id: "import-targets"));
        var target = records.CreateCustomRecord(new CustomRecordCreateRequest(targetCollection.Id, "import-target-1"));
        var relation = records.CreateCustomField(new CustomFieldCreateRequest(collection.Id, "target", "关联", "Relation",
            OptionsJson: "{\"target_collection_id\":\"import-targets\"}", Id: "import-target"));
        var mappings = new[]
        {
            new ImportFieldMapping("key", key.Id, StableKey: true),
            new ImportFieldMapping("note", note.Id),
            new ImportFieldMapping("score", score.Id),
            new ImportFieldMapping("kind", choice.Id),
            new ImportFieldMapping("target", relation.Id)
        };
        var source = Path.Combine(directory, "import.csv");
        File.WriteAllText(source, "key,note,score,kind,target\nalpha,=文本,12.5,甲," + target.Id + "\n");
        var preview = importer.Preview(new ImportRequest(collection.Id, source, "import-first", mappings));
        Check(preview.ScannedCount == 1 && preview.AcceptedCount == 1 && preview.Errors.Count == 0, "import preview validates typed values and mappings");
        var committed = importer.Commit(preview.BatchId);
        Check(committed.State == "Committed" && committed.CreatedCount == 1 && Count(database, "SELECT COUNT(*) FROM RecordChange") >= 5,
            "valid import commits one batch with auditable cell changes");
        Check(importer.Commit(preview.BatchId).State == "Committed", "repeating an import commit is idempotent");
        var importedId = preview.Rows.Single().RecordId!;
        var importedValues = new RecordWorkspaceService(database).Values(importedId);
        Check(importedValues.Single(item => item.FieldId == note.Id).TextValue == "=文本" &&
            importedValues.Single(item => item.FieldId == score.Id).NumberValue == 12.5m &&
            importedValues.Single(item => item.FieldId == choice.Id).JsonValue == "[\"import-kind-a\"]" &&
            Count(database, "SELECT COUNT(*) FROM RecordRelation WHERE FromRecordId='" + importedId + "'") == 1,
            "import preserves formula-like text, number, choice and relation values");

        var invalidSource = Path.Combine(directory, "invalid.csv");
        File.WriteAllText(invalidSource, "key,score,unknown,target\nbad,not-a-number,x,missing-target\n");
        var invalidMappings = new[] { new ImportFieldMapping("key", key.Id, StableKey: true), new ImportFieldMapping("score", score.Id), new ImportFieldMapping("target", relation.Id) };
        var invalidPreview = importer.Preview(new ImportRequest(collection.Id, invalidSource, "import-invalid", invalidMappings));
        Check(invalidPreview.RejectedCount == 1 && invalidPreview.Errors.Any(error => error.Code == "UNMAPPED_COLUMN") &&
            invalidPreview.Errors.Any(error => error.Code == "INVALID_NUMBER"), "invalid rows report unknown columns and typed errors");
        var invalidCommit = importer.Commit(invalidPreview.BatchId);
        Check(invalidCommit.State == "Failed" && Count(database, "SELECT COUNT(*) FROM RecordRef WHERE CollectionId='import-collection'") == 1,
            "a failed import batch writes no partial records");

        var updateSource = Path.Combine(directory, "update.csv");
        File.WriteAllText(updateSource, "key,note,score,kind,target\nalpha,已更新,15,甲," + target.Id + "\nbeta,新增,2,甲," + target.Id + "\n");
        var updatePreview = importer.Preview(new ImportRequest(collection.Id, updateSource, "import-update", mappings, DuplicateStrategy: "UpdateByStableKey"));
        Check(updatePreview.AcceptedCount == 2 && updatePreview.Rows[0].RecordId == importedId && !updatePreview.Rows[0].CreateRecord && updatePreview.Rows[1].CreateRecord,
            "stable-key preview separates update and append rows");
        var updateCommit = importer.Commit(updatePreview.BatchId);
        Check(updateCommit.State == "Committed" && updateCommit.UpdatedCount == 1 && updateCommit.CreatedCount == 1,
            "stable-key import updates existing rows and appends new rows");

        var changedSource = Path.Combine(directory, "changed.csv");
        File.WriteAllText(changedSource, "key,note\ngamma,first\n");
        var changedPreview = importer.Preview(new ImportRequest(collection.Id, changedSource, "import-changed", [new ImportFieldMapping("key", key.Id, StableKey: true), new ImportFieldMapping("note", note.Id)]));
        File.AppendAllText(changedSource, "gamma,second\n");
        Check(importer.Commit(changedPreview.BatchId).Errors.Any(error => error.Code == "SOURCE_CHANGED"), "changed source is rejected after preview");

        var protectedSource = Path.Combine(directory, "protected.csv");
        File.WriteAllText(protectedSource, "state\nSent\n");
        var protectedPreview = importer.Preview(new ImportRequest("system-outreach", protectedSource, "import-protected", [new ImportFieldMapping("state", "outreach-state")]));
        Check(protectedPreview.Errors.Any(error => error.Code == "SYSTEM_FIELD_IMPORT_FORBIDDEN"), "send status cannot be imported as a system field");
        Console.WriteLine("PASS: import preview, typed and relation errors, idempotent batch commit, stable-key update, source hash protection and protected send state");
        return Task.CompletedTask;
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
