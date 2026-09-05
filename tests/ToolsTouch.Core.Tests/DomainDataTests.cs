using Microsoft.Data.Sqlite;
using ToolsTouch.Application;
using ToolsTouch.Core;

static class DomainDataTests
{
    public static Task RunAsync(LocalDatabase database, string directory)
    {
        Check(new MigrationRunner(database).AvailableVersions().SequenceEqual([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]), "all sixteen migration resources are embedded");
        var repository = new OrganizationRepository(database);
        var initial = repository.Get();
        Check(initial.SchemaVersion == 16 && initial.DataRevision == 0 && initial.WorkspaceId.Length == 32, "workspace metadata initialized");

        var schoolA = repository.AddSchool(new School("school-a", "National University", "NU A", "A"));
        var schoolB = repository.AddSchool(new School("school-b", "National University", "NU B", "B"));
        var departmentA = repository.AddDepartment(new Department("department-a", schoolA.Id, "Computer Science", "Department"));
        var departmentB = repository.AddDepartment(new Department("department-b", schoolB.Id, "Computer Science", "Department"));
        Check(repository.ListDepartments(schoolA.Id, new PageRequest()).Items.Single().Id == departmentA.Id,
            "same department name is scoped to its school");

        var firstSchools = repository.ListSchools(new PageRequest(1));
        var secondSchools = repository.ListSchools(new PageRequest(1, firstSchools.NextCursor));
        Check(firstSchools.Items.Count == 1 && secondSchools.Items.Count == 1 && firstSchools.Items[0].Id != secondSchools.Items[0].Id,
            "school pagination uses a stable name and id cursor");

        var program = repository.AddProgram(new AdmissionProgram("program-a", departmentA.Id, "Computer Science", "Master"));
        repository.AddRound(new AdmissionRound("round-2026", program.Id, 2026, 2027, AdmissionRoundKind.PreRecommendation, "pre", "2026 pre-recommendation"));
        repository.AddRound(new AdmissionRound("round-2025", program.Id, 2025, 2026, AdmissionRoundKind.PreRecommendation, "pre", "2025 pre-recommendation"));
        repository.AddRound(new AdmissionRound("round-unknown", program.Id, null, null, AdmissionRoundKind.Other, "unknown", "Undated notice"));
        var firstRounds = repository.ListRounds(program.Id, new PageRequest(2));
        var secondRounds = repository.ListRounds(program.Id, new PageRequest(2, firstRounds.NextCursor));
        Check(firstRounds.Items.Select(item => item.Id).SequenceEqual(["round-2026", "round-2025"]) &&
            secondRounds.Items.Select(item => item.Id).SequenceEqual(["round-unknown"]), "round pagination keeps null years last");

        Execute(database, "INSERT INTO Professor(Id,Name,Institution,Homepage,Email,EvidenceJson,UpdatedAt) VALUES('professor-shared','Shared Professor','Legacy University','https://example.org/shared',NULL,'[]','now')");
        repository.AddAppointment(new Appointment("appointment-a", "professor-shared", departmentA.Id, role: "Advisor", isPrimary: true));
        repository.AddAppointment(new Appointment("appointment-b", "professor-shared", departmentB.Id, role: "Advisor"));
        Check(repository.ListAppointments(departmentA.Id, new PageRequest()).Items.Single().ProfessorId == "professor-shared" &&
            repository.ListAppointments(departmentB.Id, new PageRequest()).Items.Single().ProfessorId == "professor-shared",
            "one professor can have appointments in multiple departments");

        repository.AddExternalIdentity(new ExternalIdentity("source-a", "School", "same-external-id", schoolA.Id));
        repository.AddExternalIdentity(new ExternalIdentity("source-b", "School", "same-external-id", schoolB.Id));
        var beforeFailedWrite = repository.Get().DataRevision;
        Throws<SqliteException>(() => repository.AddExternalIdentity(new ExternalIdentity("source-a", "School", "same-external-id", schoolB.Id)));
        Throws<SqliteException>(() => repository.AddAppointment(new Appointment("invalid-appointment", "missing-professor", departmentA.Id)));
        Check(repository.Get().DataRevision == beforeFailedWrite && Count(database, "SELECT COUNT(*) FROM Appointment WHERE Id='invalid-appointment'") == 0,
            "failed writes roll back both entity and workspace revision");

        var legacyPath = Path.Combine(directory, "legacy-003.db");
        CreateLegacyDatabase(legacyPath);
        var legacy = new LocalDatabase(legacyPath);
        legacy.Initialize();
        legacy.Initialize();
        Check(Count(legacy, "SELECT COUNT(*) FROM SchemaVersion WHERE Version=16") == 1 &&
            Count(legacy, "SELECT COUNT(*) FROM School") == 0 &&
            Count(legacy, "SELECT COUNT(*) FROM Professor WHERE Id='legacy-professor'") == 1 &&
            Count(legacy, "SELECT COUNT(*) FROM SourceSnapshot") == 1 && Count(legacy, "SELECT COUNT(*) FROM Artifact") == 1 &&
            Count(legacy, "SELECT COUNT(*) FROM JobStage WHERE RunId='legacy-run' AND State='Completed'") == 1 &&
            Count(legacy, "SELECT COUNT(*) FROM DraftChange WHERE DraftId='legacy-draft' AND ChangeKind='Generated'") == 1,
            "a 001-003 database upgrades to 014 without losing legacy ids, source content, checkpoints or draft history");
        var legacyDraft = new OutreachService(legacy).Get("legacy-draft");
        Check(legacyDraft.Language == "zh-CN" && legacyDraft.PromptVersion == "legacy" && legacyDraft.EvidenceJson == "[]" &&
            legacyDraft.DraftRequestJson == "{}", "legacy draft receives compatible drafting defaults");
        var legacySources = new SourceRepository(legacy, new ArtifactStore(legacy.ArtifactDirectory));
        var migratedArtifact = legacySources.GetArtifact(legacySources.ListSnapshots("legacy-source-document").Single().ArtifactId!);
        Check(ArtifactStore.ReadText(migratedArtifact, legacy.ArtifactDirectory) == "Legacy body", "legacy source text is hash-addressed");
        Console.WriteLine("PASS: organization schema, workspace metadata, scoped entities, stable pagination, cross-source identities, atomic failed writes, legacy upgrade");
        return Task.CompletedTask;
    }

    private static void CreateLegacyDatabase(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        Execute(connection, "CREATE TABLE SchemaVersion (Version INTEGER PRIMARY KEY);");
        foreach (var version in new[] { 1, 2, 3 })
        {
            var name = version switch { 1 => "initial", 2 => "evidence", 3 => "sender_account", _ => throw new InvalidOperationException() };
            Execute(connection, File.ReadAllText($"src/ToolsTouch.Infrastructure/Migrations/{version:000}_{name}.sql"));
            Execute(connection, $"INSERT INTO SchemaVersion VALUES({version});");
        }
        Execute(connection, "INSERT INTO Professor(Id,Name,Institution,Homepage,Email,EvidenceJson,UpdatedAt) VALUES('legacy-professor','Legacy','Legacy University','https://example.org/legacy',NULL,'[]','now');");
        Execute(connection, "INSERT INTO Outreach(Id,ProfessorId,Version,Recipient,Subject,Body,CvPath,CvHash,State,SnapshotJson,MessageId,ThreadId,Error,IdempotencyKey,CreatedAt) VALUES('legacy-draft','legacy-professor',1,'legacy@example.org','Legacy subject','Legacy body',NULL,NULL,'Draft',NULL,NULL,NULL,NULL,'legacy-draft-key','now');");
        Execute(connection, "INSERT INTO SourceDocument VALUES('https://example.org/legacy-source','Legacy title','Legacy body','2026-09-05T10:00:00+00:00');");
        Execute(connection, "INSERT INTO AgentRun(Id,Kind,InputJson,State,Stage,CheckpointJson,BudgetJson,CreatedAt,UpdatedAt) VALUES('legacy-run','Discover','{}','Partial','Research','{\"Research\":{\"summary\":\"old checkpoint\"}}','{\"maxToolCalls\":2}','now','now');");
    }

    private static int Count(LocalDatabase database, string sql)
    {
        using var connection = database.Open();
        return Count(connection, sql);
    }

    private static int Count(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void Execute(LocalDatabase database, string sql)
    {
        using var connection = database.Open();
        Execute(connection, sql);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
