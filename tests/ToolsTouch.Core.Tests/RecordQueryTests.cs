using System.Text.Json;
using ToolsTouch.Application;
using ToolsTouch.Core;
using ToolsTouch.Infrastructure.Tracking;

static class RecordQueryTests
{
    public static Task RunAsync(LocalDatabase database)
    {
        var catalog = new SystemCollectionAdapter(database);
        catalog.EnsureSystemCatalog();
        var records = new RecordWorkspaceService(database);
        var collection = records.CreateCustomCollection(new CustomCollectionCreateRequest("查询验收表", Id: "query-collection"));
        var title = records.CreateCustomField(new CustomFieldCreateRequest(collection.Id, "title", "标题", "Text", Id: "query-title"));
        var score = records.CreateCustomField(new CustomFieldCreateRequest(collection.Id, "score", "分数", "Number", Id: "query-score"));
        var relationCollection = records.CreateCustomCollection(new CustomCollectionCreateRequest("查询关联目标", Id: "query-targets"));
        var relation = records.CreateCustomField(new CustomFieldCreateRequest(collection.Id, "target", "目标", "Relation",
            OptionsJson: "{\"target_collection_id\":\"query-targets\"}", Id: "query-target"));
        var target = records.CreateCustomRecord(new CustomRecordCreateRequest(relationCollection.Id, "query-target-1"));
        var first = records.CreateCustomRecord(new CustomRecordCreateRequest(collection.Id, "query-record-1"));
        var second = records.CreateCustomRecord(new CustomRecordCreateRequest(collection.Id, "query-record-2"));
        var third = records.CreateCustomRecord(new CustomRecordCreateRequest(collection.Id, "query-record-3"));
        var empty = records.CreateCustomRecord(new CustomRecordCreateRequest(collection.Id, "query-record-4"));

        first = Write(records, first, title, new TypedRecordValue("Text", TextValue: "Alpha interview"), "query-title-1");
        first = Write(records, first, score, new TypedRecordValue("Number", NumberValue: 10m), "query-score-1");
        first = Write(records, first, relation, new TypedRecordValue("Relation", RelationRecordIds: [target.Id]), "query-relation-1");
        second = Write(records, second, title, new TypedRecordValue("Text", TextValue: "Alphabet plan"), "query-title-2");
        second = Write(records, second, score, new TypedRecordValue("Number", NumberValue: 10m), "query-score-2");
        third = Write(records, third, title, new TypedRecordValue("Text", TextValue: "Beta plan"), "query-title-3");
        third = Write(records, third, score, new TypedRecordValue("Number", NumberValue: 5m), "query-score-3");

        var queries = new RecordQueryService(database);
        var filtered = queries.Query(new RecordQueryRequest(collection.Id,
            FilterAstJson: "{\"kind\":\"and\",\"children\":[{\"field_id\":\"query-title\",\"operator\":\"contains\",\"value\":\"alpha\"},{\"field_id\":\"query-score\",\"operator\":\"gte\",\"value\":10}]}",
            Limit: 10));
        Check(filtered.Items.Select(item => item.Record.Id).SequenceEqual([first.Id, second.Id]), "compound filter uses typed values and parameters");
        Check(filtered.Items[0].Value(title.Id)?.TextValue == "Alpha interview", "query rows include custom typed values");

        var related = queries.Query(new RecordQueryRequest(collection.Id,
            FilterAstJson: "{\"field_id\":\"query-target\",\"operator\":\"eq\",\"value\":\"query-target-1\"}"));
        Check(related.Items.Select(item => item.Record.Id).SequenceEqual([first.Id]), "relation filters use EXISTS and stable RecordRef IDs");

        var sort = "[{\"field_id\":\"query-score\",\"direction\":\"desc\",\"nulls\":\"last\"}]";
        var pageOne = queries.Query(new RecordQueryRequest(collection.Id, SortJson: sort, Limit: 2));
        var pageTwo = queries.Query(new RecordQueryRequest(collection.Id, SortJson: sort, Limit: 2, After: pageOne.NextCursor));
        Check(pageOne.Items.Select(item => item.Record.Id).SequenceEqual([first.Id, second.Id]) &&
            pageTwo.Items.Select(item => item.Record.Id).SequenceEqual([third.Id, empty.Id]) && pageOne.NextCursor is not null,
            "sorted pagination keeps equal sort keys and nulls in deterministic order");
        Check(pageTwo.Items.Single(item => item.Record.Id == empty.Id).Value(score.Id) is null, "null sort values remain null");

        var saved = new ViewService(database).Save(new ViewSaveRequest(collection.Id, "Alpha view", FilterAstJson: FilterBuilder.Condition(title.Id, "contains", "alpha"), SortJson: sort));
        var restored = new RecordQueryService(database).Query(new RecordQueryRequest(collection.Id, ViewId: saved.Id, Limit: 10));
        Check(restored.Items.Select(item => item.Record.Id).SequenceEqual([first.Id, second.Id]), "saved view survives a fresh service instance");
        var updated = new ViewService(database).Save(new ViewSaveRequest(collection.Id, "Alpha view", FilterAstJson: FilterBuilder.Condition(title.Id, "eq", "Alpha interview"),
            SortJson: sort, Id: saved.Id, ExpectedRevision: saved.Revision));
        Check(updated.Revision == saved.Revision + 1 && new RecordQueryService(database).Query(new RecordQueryRequest(collection.Id, ViewId: updated.Id, Limit: 10)).Items.Select(item => item.Record.Id).SequenceEqual([first.Id]),
            "view updates use revision CAS and change the restored query");
        ThrowsCode(() => new ViewService(database).Save(new ViewSaveRequest(collection.Id, "stale", Id: saved.Id, ExpectedRevision: saved.Revision)), "VIEW_CHANGED");
        var copied = new ViewService(database).Copy(new ViewCopyRequest(saved.Id, "Copied alpha view", "query-view-copy"));
        Check(new ViewService(database).List(collection.Id).Select(item => item.Id).Contains(copied.Id), "views can be copied without copying records");

        ThrowsCode(() => queries.Query(new RecordQueryRequest(collection.Id,
            FilterAstJson: "{\"field_id\":\"query-title; DROP TABLE FieldValue\",\"operator\":\"eq\",\"value\":\"x\"}")), "FILTER_FIELD_NOT_ALLOWED");
        ThrowsCode(() => queries.Query(new RecordQueryRequest(collection.Id,
            FilterAstJson: Nested("and", 6))), "FILTER_DEPTH_EXCEEDED");
        ThrowsCode(() => queries.Query(new RecordQueryRequest(collection.Id,
            FilterAstJson: ManyLeaves(title.Id, 101))), "FILTER_NODE_LIMIT_EXCEEDED");
        ThrowsCode(() => queries.Query(new RecordQueryRequest(collection.Id, SortJson: "[{\"field_id\":\"query-target\",\"direction\":\"asc\"}]")), "SORT_RELATION_UNSUPPORTED");

        using (var connection = database.Open())
        using (var command = LocalDatabase.Command(connection, "INSERT INTO School(Id,CanonicalName,ShortName,City,Revision,CreatedAt,UpdatedAt) VALUES('query-school','Query University','QU','Query City',1,'now','now')"))
            command.ExecuteNonQuery();
        catalog.EnsureSystemCatalog();
        var schoolRows = queries.Query(new RecordQueryRequest("system-schools",
            FilterAstJson: "{\"field_id\":\"school-name\",\"operator\":\"contains\",\"value\":\"query university\"}"));
        Check(schoolRows.Items.Any(item => item.Record.Id == "system-schools:query-school" && item.Value("school-name")?.TextValue == "Query University"),
            "system field filters use the whitelisted domain binding and expose current domain values");

        Console.WriteLine("PASS: filter AST limits, SQL whitelist, typed and relation filters, null sorting, tied keyset pagination and saved view restore");
        return Task.CompletedTask;
    }

    private static RecordRefRecord Write(RecordWorkspaceService service, RecordRefRecord record, FieldDefinitionRecord field, TypedRecordValue value, string commandId)
    {
        var result = service.UpdateCell(new UpdateCellCommand(record.Id, field.Id, record.Revision, field.Revision, value, commandId));
        return record with { Revision = result.RecordRevision };
    }

    private static string Nested(string kind, int depth)
    {
        var node = "{\"field_id\":\"query-title\",\"operator\":\"eq\",\"value\":\"x\"}";
        for (var index = 0; index < depth; index++) node = "{\"kind\":\"" + kind + "\",\"children\":[" + node + "]}";
        return node;
    }

    private static string ManyLeaves(string fieldId, int count) =>
        JsonSerializer.Serialize(new { kind = "and", children = Enumerable.Range(0, count).Select(_ => new { field_id = fieldId, @operator = "isEmpty" }) });

    private static void ThrowsCode(Action action, string code)
    {
        try { action(); }
        catch (InvalidOperationException error) when (error.Message == code || error.Message.StartsWith(code + ":", StringComparison.Ordinal)) { return; }
        throw new Exception("Expected " + code);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
