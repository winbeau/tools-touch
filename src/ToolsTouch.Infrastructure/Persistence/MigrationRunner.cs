using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ToolsTouch.Core;

public sealed class MigrationRunner
{
    private readonly LocalDatabase database;
    private readonly Assembly assembly;

    public MigrationRunner(LocalDatabase database, Assembly? assembly = null)
    {
        this.database = database;
        this.assembly = assembly ?? typeof(LocalDatabase).Assembly;
    }

    public IReadOnlyList<int> AvailableVersions() => LoadMigrations().Select(item => item.Version).ToArray();

    public void Run()
    {
        using var connection = database.Open();
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER PRIMARY KEY);";
            setup.ExecuteNonQuery();
        }

        var migrations = LoadMigrations();
        var legacySources = migrations.Any(item => item.Version == 5) && !IsApplied(connection, 5)
            ? PrepareLegacySources(connection) : [];
        // Migration 007 rebuilds Professor to relax Homepage nullability. SQLite cannot
        // toggle foreign_keys inside a transaction, so disable enforcement around the
        // complete migration transaction and restore it before the connection is reused.
        using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys=OFF";
            foreignKeys.ExecuteNonQuery();
        }
        try
        {
            using var transaction = connection.BeginTransaction();
            foreach (var migration in migrations)
            {
                if (IsApplied(connection, transaction, migration.Version)) continue;

                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                command.ExecuteNonQuery();
                if (migration.Version == 5) InsertLegacySources(connection, transaction, legacySources);
                if (migration.Version == 6) BackfillLegacyJobs(connection, transaction);
                using var applied = connection.CreateCommand();
                applied.Transaction = transaction;
                applied.CommandText = "INSERT INTO SchemaVersion(Version) VALUES($version)";
                applied.Parameters.AddWithValue("$version", migration.Version);
                applied.ExecuteNonQuery();
                if (migration.Version >= 4)
                {
                    using var metadata = connection.CreateCommand();
                    metadata.Transaction = transaction;
                    metadata.CommandText = "UPDATE WorkspaceMeta SET SchemaVersion=$version WHERE Id=1";
                    metadata.Parameters.AddWithValue("$version", migration.Version);
                    if (metadata.ExecuteNonQuery() != 1) throw new InvalidOperationException("WORKSPACE_META_MISSING");
                }
            }
            transaction.Commit();
        }
        finally
        {
            using var foreignKeys = connection.CreateCommand();
            foreignKeys.CommandText = "PRAGMA foreign_keys=ON";
            foreignKeys.ExecuteNonQuery();
        }
    }

    private static bool IsApplied(SqliteConnection connection, int version)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM SchemaVersion WHERE Version=$version";
        command.Parameters.AddWithValue("$version", version);
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    private static bool IsApplied(SqliteConnection connection, SqliteTransaction transaction, int version)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM SchemaVersion WHERE Version=$version";
        command.Parameters.AddWithValue("$version", version);
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    private IReadOnlyList<LegacySource> PrepareLegacySources(SqliteConnection connection)
    {
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='SourceDocument'";
        if (Convert.ToInt32(exists.ExecuteScalar()) == 0) return [];
        var result = new List<LegacySource>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Url,Title,Text,FetchedAt FROM SourceDocument ORDER BY Url";
        using var reader = command.ExecuteReader();
        var store = new ArtifactStore(database.ArtifactDirectory);
        while (reader.Read())
        {
            var url = reader.GetString(0);
            var title = reader.GetString(1);
            var text = reader.GetString(2);
            var fetchedAt = DateTimeOffset.TryParse(reader.GetString(3), out var parsed) ? parsed : DateTimeOffset.UtcNow;
            var artifact = store.Stage("legacy-source-document", Encoding.UTF8.GetBytes(text), "text/plain; charset=utf-8", ".txt");
            var snapshotId = "legacy-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url + "\0" + artifact.ContentHash))).ToLowerInvariant();
            var snapshot = new SourceSnapshot(snapshotId, "legacy-source-document", url, url, url, artifact.ContentHash,
                fetchedAt, null, null, "legacy", "legacy-005", artifact.Id, title);
            result.Add(new LegacySource(artifact, snapshot));
        }
        return result;
    }

    private static void InsertLegacySources(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<LegacySource> sources)
    {
        foreach (var source in sources)
        {
            ArtifactStore.Insert(connection, transaction, source.Artifact);
            using var command = LocalDatabase.Command(connection, """
                INSERT INTO SourceSnapshot(Id,SourceKey,OriginalUrl,CanonicalUrl,ExternalRecordId,ContentHash,FetchedAt,PublishedAt,SourceYear,Transport,ArtifactId,ParseVersion,Title)
                VALUES($id,$source,$original,$canonical,$external,$hash,$fetched,$published,$year,$transport,$artifact,$parse,$title)
                """, ("$id", source.Snapshot.Id), ("$source", source.Snapshot.SourceKey), ("$original", source.Snapshot.OriginalUrl),
                ("$canonical", source.Snapshot.CanonicalUrl), ("$external", source.Snapshot.ExternalRecordId), ("$hash", source.Snapshot.ContentHash),
                ("$fetched", source.Snapshot.FetchedAt.ToString("O")), ("$published", source.Snapshot.PublishedAt?.ToString("O")),
                ("$year", source.Snapshot.SourceYear), ("$transport", source.Snapshot.Transport), ("$artifact", source.Snapshot.ArtifactId),
                ("$parse", source.Snapshot.ParseVersion), ("$title", source.Snapshot.Title));
            command.Transaction = transaction;
            command.ExecuteNonQuery();
        }
    }

    private static void BackfillLegacyJobs(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var read = LocalDatabase.Command(connection, "SELECT Id,Kind,State,Stage,InputJson,BudgetJson,CheckpointJson,Error,CreatedAt,UpdatedAt FROM AgentRun ORDER BY CreatedAt,Id");
        read.Transaction = transaction;
        using var reader = read.ExecuteReader();
        var runs = new List<LegacyRun>();
        while (reader.Read()) runs.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8), reader.GetString(9)));
        reader.Close();
        foreach (var run in runs)
        {
            var budget = ReadLegacyBudget(run.BudgetJson);
            using (var jobBudget = LocalDatabase.Command(connection, """
                INSERT OR IGNORE INTO JobBudget(RunId,RootRunId,MaxToolCalls,MaxModelRequests,MaxRuntimeMilliseconds)
                VALUES($run,$run,$tools,$models,$runtime)
                """, ("$run", run.Id), ("$tools", budget.MaxToolCalls), ("$models", budget.MaxModelRequests), ("$runtime", budget.MaxRuntimeMilliseconds)))
            { jobBudget.Transaction = transaction; jobBudget.ExecuteNonQuery(); }
            var checkpoint = ParseObject(run.CheckpointJson);
            var stages = LegacyStages(run.Kind, run.Stage);
            foreach (var (stageKey, ordinal) in stages)
            {
                var completed = checkpoint?.TryGetProperty(stageKey, out _) == true || run.State == "Completed" && run.Stage == "Done";
                var state = completed ? "Completed" : run.State switch
                {
                    "Cancelled" => "Cancelled", "Failed" => "Failed", "Partial" => "Queued", "Running" => "Queued", _ => "Queued"
                };
                var inputHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(run.InputJson + "\0" + stageKey))).ToLowerInvariant();
                var output = checkpoint is { } saved && saved.TryGetProperty(stageKey, out var savedOutput) ? savedOutput.GetRawText() : null;
                using var stage = LocalDatabase.Command(connection, """
                    INSERT OR IGNORE INTO JobStage(Id,RunId,StageKey,Ordinal,State,AttemptCount,OutputJson,Error,StableKey,InputHash,CreatedAt,UpdatedAt)
                    VALUES($id,$run,$key,$ordinal,$state,0,$output,$error,$stable,$hash,$created,$updated)
                    """, ("$id", "legacy-stage-" + run.Id + "-" + ordinal), ("$run", run.Id), ("$key", stageKey), ("$ordinal", ordinal),
                    ("$state", state), ("$output", output),
                    ("$error", completed ? null : run.Error), ("$stable", run.Id + ":" + stageKey), ("$hash", inputHash), ("$created", run.CreatedAt), ("$updated", run.UpdatedAt));
                stage.Transaction = transaction;
                stage.ExecuteNonQuery();
            }
            if (run.State == "Running")
            {
                using var update = LocalDatabase.Command(connection, "UPDATE AgentRun SET State='Queued',Error='PROCESS_INTERRUPTED' WHERE Id=$run", ("$run", run.Id));
                update.Transaction = transaction; update.ExecuteNonQuery();
            }
        }
    }

    private static IReadOnlyList<(string Key, int Ordinal)> LegacyStages(string kind, string currentStage) => kind switch
    {
        "Discover" => [("Research", 0)],
        "Analyze" => [("Read", 0), ("Analyze", 1)],
        "Draft" => [("Draft", 0)],
        _ => [(string.IsNullOrWhiteSpace(currentStage) ? "Prepare" : currentStage, 0)]
    };

    private static JobBudgetDefaults ReadLegacyBudget(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new(root.TryGetProperty("maxToolCalls", out var tools) && tools.TryGetInt32(out var toolCount) ? Math.Clamp(toolCount, 1, 100) : 30,
                root.TryGetProperty("maxModelRequests", out var models) && models.TryGetInt32(out var modelCount) ? Math.Max(1, modelCount) : 100,
                root.TryGetProperty("timeoutMs", out var timeout) && timeout.TryGetInt32(out var timeoutValue) ? Math.Clamp(timeoutValue, 1000, 900000) : 900000);
        }
        catch (JsonException) { return new(30, 100, 900000); }
    }

    private static JsonElement? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    private IReadOnlyList<Migration> LoadMigrations()
    {
        var migrations = new List<Migration>();
        foreach (var resource in assembly.GetManifestResourceNames()
                     .Where(name => name.Contains(".Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal)))
        {
            var name = resource.Split(".Migrations.", 2, StringSplitOptions.None)[1];
            var separator = name.IndexOf('_');
            if (separator <= 0 || !int.TryParse(name[..separator], out var version) || version <= 0)
                throw new InvalidOperationException("INVALID_MIGRATION_RESOURCE: " + resource);
            using var stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException("MIGRATION_RESOURCE_MISSING: " + resource);
            using var reader = new StreamReader(stream);
            migrations.Add(new Migration(version, reader.ReadToEnd(), resource));
        }

        var duplicate = migrations.GroupBy(item => item.Version).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new InvalidOperationException("DUPLICATE_MIGRATION_VERSION: " + duplicate.Key);
        return migrations.OrderBy(item => item.Version).ToArray();
    }

    private sealed record Migration(int Version, string Sql, string Resource);
    private sealed record LegacySource(Artifact Artifact, SourceSnapshot Snapshot);
    private sealed record LegacyRun(string Id, string Kind, string State, string Stage, string InputJson, string BudgetJson, string? CheckpointJson, string? Error, string CreatedAt, string UpdatedAt);
    private sealed record JobBudgetDefaults(int MaxToolCalls, int MaxModelRequests, int MaxRuntimeMilliseconds);
}
