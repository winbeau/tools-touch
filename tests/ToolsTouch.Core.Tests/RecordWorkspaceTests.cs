using ToolsTouch.Application;
using ToolsTouch.Core;
using ToolsTouch.Infrastructure.Tracking;

static class RecordWorkspaceTests
{
    public static Task RunAsync(LocalDatabase database)
    {
        var adapter = new SystemCollectionAdapter(database);
        var before = new OrganizationRepository(database).Get().DataRevision;
        var collections = adapter.EnsureSystemCatalog();
        Check(collections.Count == 8 && collections.All(item => item.Kind == "System" && item.SystemEntityKind is not null),
            "system collections are versioned metadata rather than duplicated business tables");
        var schools = adapter.Records("system-schools");
        Check(schools.Count >= 2 && schools.All(item => item.EntityKind == "School" && item.EntityId is not null) && schools.Any(item => item.EntityId == "school-a"),
            $"system record references map each school to its domain id (count={schools.Count}; values={string.Join(',', schools.Select(item => $"{item.EntityKind}:{item.EntityId}"))})");
        var applications = adapter.Records("system-applications");
        Check(applications.Count == 1 && applications[0].EntityId == "application-case-1",
            "application records are mapped through RecordRef after the domain service creates them");

        var applicationFields = adapter.Fields("system-applications");
        var stage = applicationFields.Single(item => item.Key == "stage");
        var priority = applicationFields.Single(item => item.Key == "priority");
        Check(applicationFields.All(item => item.StorageKind == "System" && item.SystemBinding is not null) &&
            stage.Type == "Choice" && stage.EditPolicy == "OverrideWithReason" && priority.EditPolicy == "Editable",
            "system bindings expose explicit field permissions without a second FieldValue copy");
        Check(adapter.Fields("system-schools").Single(item => item.Key == "name").EditPolicy == "SystemManaged" &&
            Count(database, "SELECT COUNT(*) FROM FieldValue") == 0,
            "domain values remain in typed domain tables and system state is read-only metadata");

        var afterFirst = new OrganizationRepository(database).Get().DataRevision;
        adapter.EnsureSystemCatalog();
        var afterSecond = new OrganizationRepository(database).Get().DataRevision;
        Check(afterFirst >= before && afterSecond == afterFirst && adapter.Records("system-schools").Count == schools.Count,
            "system catalog synchronization is idempotent and does not create duplicate references");
        Console.WriteLine("PASS: record workspace schema, system collections, stable RecordRef mapping and field edit policies");
        return Task.CompletedTask;
    }

    private static int Count(LocalDatabase database, string sql)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
