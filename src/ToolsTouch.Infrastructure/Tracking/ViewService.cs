using System.Text.Json;
using Microsoft.Data.Sqlite;
using ToolsTouch.Application;
using ToolsTouch.Core;

namespace ToolsTouch.Infrastructure.Tracking;

public sealed class ViewService(LocalDatabase database) : IViewService
{
    private static string Now => DateTimeOffset.UtcNow.ToString("O");

    public IReadOnlyList<ViewDefinitionRecord> List(string collectionId)
    {
        Required(collectionId, nameof(collectionId));
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, SelectSql + " WHERE CollectionId=$collection ORDER BY Name COLLATE NOCASE,Id", ("$collection", collectionId));
        using var reader = command.ExecuteReader();
        var result = new List<ViewDefinitionRecord>();
        while (reader.Read()) result.Add(Read(reader));
        return result;
    }

    public ViewDefinitionRecord Get(string viewId)
    {
        Required(viewId, nameof(viewId));
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, SelectSql + " WHERE Id=$id", ("$id", viewId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("VIEW_NOT_FOUND");
        return Read(reader);
    }

    public ViewDefinitionRecord Save(ViewSaveRequest request)
    {
        var collectionId = Required(request.CollectionId, nameof(request.CollectionId));
        var name = Required(request.Name, nameof(request.Name));
        var viewType = Required(request.ViewType, nameof(request.ViewType));
        if (!string.Equals(viewType, "Grid", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("VIEW_TYPE_UNSUPPORTED");
        var filter = JsonObject(request.FilterAstJson, "{}", "VIEW_FILTER_INVALID");
        var sort = JsonArray(request.SortJson, "[]", "VIEW_SORT_INVALID");
        var group = JsonObject(request.GroupJson, "{}", "VIEW_GROUP_INVALID");
        var columns = JsonArray(request.ColumnsJson, "[]", "VIEW_COLUMNS_INVALID");
        var catalog = new SystemCollectionAdapter(database);
        if (catalog.Collections().All(collection => collection.Id != collectionId || collection.ArchivedAt is not null))
            throw new KeyNotFoundException("COLLECTION_NOT_FOUND");
        var fields = catalog.Fields(collectionId);
        try { RecordQueryCompiler.Compile(collectionId, fields, filter, sort); }
        catch (InvalidOperationException error) { throw new InvalidOperationException("VIEW_QUERY_INVALID: " + error.Message, error); }

        var id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : Required(request.Id, nameof(request.Id));
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var exists = request.ExpectedRevision is not null;
        if (exists && request.Id is null) throw new ArgumentException("An existing view id is required for update.", nameof(request.Id));
        if (!exists)
        {
            using var insert = LocalDatabase.Command(connection, """
                INSERT INTO ViewDefinition(Id,CollectionId,Name,ViewType,FilterAstJson,SortJson,GroupJson,ColumnsJson,Revision,CreatedAt,UpdatedAt)
                VALUES($id,$collection,$name,$type,$filter,$sort,$group,$columns,1,$now,$now)
                """, ("$id", id), ("$collection", collectionId), ("$name", name), ("$type", "Grid"), ("$filter", filter),
                ("$sort", sort), ("$group", group), ("$columns", columns), ("$now", Now));
            insert.Transaction = transaction;
            insert.ExecuteNonQuery();
        }
        else
        {
            if (request.ExpectedRevision is null or <= 0) throw new ArgumentOutOfRangeException(nameof(request.ExpectedRevision));
            using var update = LocalDatabase.Command(connection, """
                UPDATE ViewDefinition SET Name=$name,ViewType=$type,FilterAstJson=$filter,SortJson=$sort,GroupJson=$group,ColumnsJson=$columns,
                    Revision=Revision+1,UpdatedAt=$now
                WHERE Id=$id AND CollectionId=$collection AND Revision=$revision
                """, ("$id", id), ("$collection", collectionId), ("$name", name), ("$type", "Grid"), ("$filter", filter),
                ("$sort", sort), ("$group", group), ("$columns", columns), ("$now", Now), ("$revision", request.ExpectedRevision.Value));
            update.Transaction = transaction;
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("VIEW_CHANGED");
        }
        IncrementRevision(connection, transaction);
        transaction.Commit();
        return Get(id);
    }

    public ViewDefinitionRecord Copy(ViewCopyRequest request)
    {
        var source = Get(request.SourceViewId);
        return Save(new ViewSaveRequest(source.CollectionId, request.Name, source.ViewType, source.FilterAstJson,
            source.SortJson, source.GroupJson, source.ColumnsJson, request.Id));
    }

    private const string SelectSql = "SELECT Id,CollectionId,Name,ViewType,FilterAstJson,SortJson,GroupJson,ColumnsJson,Revision FROM ViewDefinition";

    private static ViewDefinitionRecord Read(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetInt32(8));

    private static string JsonObject(string? json, string fallback, string errorCode) => JsonContainer(json, fallback, JsonValueKind.Object, errorCode);
    private static string JsonArray(string? json, string fallback, string errorCode) => JsonContainer(json, fallback, JsonValueKind.Array, errorCode);

    private static string JsonContainer(string? json, string fallback, JsonValueKind kind, string errorCode)
    {
        var value = string.IsNullOrWhiteSpace(json) ? fallback : json.Trim();
        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != kind) throw new InvalidOperationException(errorCode);
            return document.RootElement.GetRawText();
        }
        catch (JsonException error) { throw new InvalidOperationException(errorCode, error); }
    }

    private static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A value is required.", name) : value.Trim();

    private static void IncrementRevision(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = LocalDatabase.Command(connection, "UPDATE WorkspaceMeta SET DataRevision=DataRevision+1 WHERE Id=1");
        command.Transaction = transaction;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("WORKSPACE_META_MISSING");
    }
}
