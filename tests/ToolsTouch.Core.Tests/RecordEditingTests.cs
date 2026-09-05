using ToolsTouch.Application;
using ToolsTouch.Core;
using ToolsTouch.Infrastructure.Tracking;

static class RecordEditingTests
{
    public static Task RunAsync(LocalDatabase database)
    {
        var service = new RecordWorkspaceService(database);
        var catalog = new SystemCollectionAdapter(database);
        catalog.EnsureSystemCatalog();
        var collection = service.CreateCustomCollection(new CustomCollectionCreateRequest("面试安排", "自定义面试记录", "custom-interviews"));
        var name = service.CreateCustomField(new CustomFieldCreateRequest(collection.Id, "candidate", "候选人", "Text", Id: "field-candidate"));
        var score = service.CreateCustomField(new CustomFieldCreateRequest(collection.Id, "score", "评分", "Number", Id: "field-score"));
        var state = service.CreateCustomField(new CustomFieldCreateRequest(collection.Id, "state", "状态", "Choice", Id: "field-state"));
        var firstChoice = service.CreateChoice(new FieldChoiceCreateRequest(state.Id, "待安排", SortOrder: 1, Id: "choice-pending"));
        service.CreateChoice(new FieldChoiceCreateRequest(state.Id, "已完成", SortOrder: 2, Id: "choice-done"));
        var relation = service.CreateCustomField(new CustomFieldCreateRequest(collection.Id, "professor", "导师", "Relation", OptionsJson: "{\"target_collection_id\":\"system-professors\"}", Id: "field-professor"));
        var first = service.CreateCustomRecord(new CustomRecordCreateRequest(collection.Id, "interview-1"));
        var second = service.CreateCustomRecord(new CustomRecordCreateRequest(collection.Id, "interview-2"));
        var professor = catalog.Records("system-professors").First();

        var written = service.UpdateCell(new UpdateCellCommand(first.Id, name.Id, first.Revision, name.Revision,
            new TypedRecordValue("Text", TextValue: "导师面试"), "cell-name-1"));
        Check(written.RecordRevision == 2 && service.Values(first.Id).Single(item => item.FieldId == name.Id).TextValue == "导师面试",
            "custom text cells persist with a single typed value slot and increment RecordRef revision");
        var idempotent = service.UpdateCell(new UpdateCellCommand(first.Id, name.Id, first.Revision, name.Revision,
            new TypedRecordValue("Text", TextValue: "导师面试"), "cell-name-1"));
        Check(idempotent.ChangeId == written.ChangeId && Count(database, "SELECT COUNT(*) FROM RecordChange WHERE CommandId='cell-name-1'") == 1,
            "repeating a cell command is idempotent");
        ThrowsCode(() => service.UpdateCell(new UpdateCellCommand(first.Id, name.Id, first.Revision, name.Revision,
            new TypedRecordValue("Text", TextValue: "过期"), "cell-name-stale")), "RECORD_CHANGED");

        var scoreWritten = service.UpdateCell(new UpdateCellCommand(first.Id, score.Id, written.RecordRevision, score.Revision,
            new TypedRecordValue("Number", NumberValue: 8.5m), "cell-score-1"));
        var choiceWritten = service.UpdateCell(new UpdateCellCommand(first.Id, state.Id, scoreWritten.RecordRevision, state.Revision,
            new TypedRecordValue("Choice", ChoiceIds: [firstChoice.Id]), "cell-state-1"));
        Check(choiceWritten.Value?.ChoiceIds?.Single() == firstChoice.Id, "choice values validate against the field's Choice rows");
        ThrowsCode(() => service.UpdateCell(new UpdateCellCommand(first.Id, state.Id, choiceWritten.RecordRevision, state.Revision,
            new TypedRecordValue("Choice", ChoiceIds: ["missing-choice"]), "cell-state-invalid")), "CHOICE_NOT_FOUND");
        var related = service.UpdateCell(new UpdateCellCommand(first.Id, relation.Id, choiceWritten.RecordRevision, relation.Revision,
            new TypedRecordValue("Relation", RelationRecordIds: [professor.Id]), "cell-relation-1"));
        Check(related.Value?.RelationRecordIds?.Single() == professor.Id && service.Relations(first.Id, relation.Id).Single().ToRecordId == professor.Id,
            "relation values validate target RecordRef collection and use RecordRelation");

        var bulk = service.BulkEdit(new BulkEditRequest([
            new UpdateCellCommand(first.Id, score.Id, related.RecordRevision, score.Revision, new TypedRecordValue("Number", NumberValue: 9m), "bulk:0"),
            new UpdateCellCommand(second.Id, score.Id, second.Revision, score.Revision, new TypedRecordValue("Number", NumberValue: 7m), "bulk:1")
        ], "bulk-score"));
        Check(bulk.Applied == 2 && bulk.Results.All(item => item.FieldId == score.Id) && service.Values(second.Id).Single(item => item.FieldId == score.Id).NumberValue == 7m,
            "bulk edit applies a fixed record set with per-cell CAS and one transaction");

        var changed = service.UpdateCell(new UpdateCellCommand(first.Id, name.Id, bulk.Results.Single(item => item.RecordId == first.Id).RecordRevision, name.Revision,
            new TypedRecordValue("Text", TextValue: "修改后的面试"), "cell-name-2"));
        var undone = service.Undo(changed.ChangeId, "undo-cell-name-2", changed.RecordRevision, "撤销最近一次自定义编辑");
        Check(undone.Value?.TextValue == "导师面试" && service.Values(first.Id).Single(item => item.FieldId == name.Id).TextValue == "导师面试",
            "undo is a conditional reverse command that preserves the original change record");

        var systemField = catalog.Fields("system-applications").Single(item => item.Key == "stage");
        ThrowsCode(() => service.UpdateCell(new UpdateCellCommand("system-applications:application-case-1", systemField.Id, 1, systemField.Revision,
            new TypedRecordValue("Choice", ChoiceIds: [firstChoice.Id]), "system-stage-forbidden")), "SYSTEM_FIELD_DOMAIN_COMMAND_REQUIRED");
        Console.WriteLine("PASS: custom typed cells, choices, relations, per-record CAS, idempotent bulk edit, undo and protected system fields");
        return Task.CompletedTask;
    }

    private static int Count(LocalDatabase database, string sql)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void ThrowsCode(Action action, string code)
    {
        try { action(); }
        catch (InvalidOperationException error) when (error.Message == code) { return; }
        throw new Exception("Expected " + code);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
