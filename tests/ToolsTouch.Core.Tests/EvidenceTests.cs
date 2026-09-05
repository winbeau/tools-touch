using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using ToolsTouch.Core;

static class EvidenceTests
{
    public static Task RunAsync(LocalDatabase database, string directory)
    {
        var artifactStore = new ArtifactStore(database.ArtifactDirectory);
        var repository = new SourceRepository(database, artifactStore);
        var firstBytes = Encoding.UTF8.GetBytes("official page version one");
        var firstHash = Convert.ToHexString(SHA256.HashData(firstBytes)).ToLowerInvariant();
        var first = repository.SaveSnapshot(new SourceSnapshot("snapshot-one", "university-site", "https://example.org/admissions", "https://example.org/admissions", "admissions", firstHash,
            DateTimeOffset.Parse("2026-09-05T10:00:00Z"), DateTimeOffset.Parse("2026-08-01T00:00:00Z"), 2026, "https", "parser-1", title: "Admissions one"),
            firstBytes, "source-page", "text/html", ".html");
        var secondBytes = Encoding.UTF8.GetBytes("official page version two");
        var secondHash = Convert.ToHexString(SHA256.HashData(secondBytes)).ToLowerInvariant();
        var second = repository.SaveSnapshot(new SourceSnapshot("snapshot-two", "university-site", "https://example.org/admissions", "https://example.org/admissions", "admissions", secondHash,
            DateTimeOffset.Parse("2026-09-05T11:00:00Z"), null, null, "https", "parser-2", title: "Admissions two"),
            secondBytes, "source-page", "text/html", ".html");
        Check(first.ArtifactId != second.ArtifactId && repository.ListSnapshots("university-site", "admissions").Count == 2,
            "source revisions append immutable snapshots");
        Check(ArtifactStore.ReadText(repository.GetArtifact(first.ArtifactId!), artifactStore.RootDirectory) == "official page version one" &&
            ArtifactStore.ReadText(repository.GetArtifact(second.ArtifactId!), artifactStore.RootDirectory) == "official page version two",
            "artifact content is addressed and retained by hash");

        var claim = repository.AddClaim(new EvidenceClaim("claim-one", first.Id, "AdmissionRound", "round-1", "registration_end",
            "{\"raw\":\"2026-09-20\"}", "报名截止：2026-09-20", "{\"line\":12}", "OfficialFact", "High"),
            [new ClaimSupport("claim-one", first.Id, "{\"line\":12}"), new ClaimSupport("claim-one", second.Id, "{\"line\":13}")]);
        Check(repository.ListClaims("AdmissionRound", "round-1").Single().Id == claim.Id, "claims retain subject and source locator");
        repository.AddSourceCheck(new SourceCheck("check-one", second.Id, DateTimeOffset.UtcNow, "Changed", ETag: "etag-2"));
        repository.AddConflict(new EvidenceConflict("conflict-one", "AdmissionRound", "round-1", "registration_end", "[\"claim-one\"]"));
        var observation = repository.AddObservation(new WindowObservation("observation-one", "round-2026", first.Id, 2026, 2026, 2027,
            "Official", "Official", "2026-09-01", "2026-09-20", null, null,
            null, null, null, null, null, null, null, null, DatePrecision.Date, "Asia/Shanghai", DateTimeOffset.UtcNow));
        repository.AddProjection(new HistoricalProjection("projection-one", "program-a", 2027, "PreRecommendation", "pre",
            observation.Id, "2027-09-01", "2027-09-20", "projection-1", DateTimeOffset.UtcNow, null));
        repository.AddPreferenceRevision(new PreferenceRevision("preference-one", 1, "Prefer systems", "{\"degree\":\"Master\"}", true, DateTimeOffset.UtcNow));
        Check(Count(database, "SELECT COUNT(*) FROM SourceCheck") == 1 && Count(database, "SELECT COUNT(*) FROM EvidenceConflict") == 1 &&
            Count(database, "SELECT COUNT(*) FROM WindowObservation") == 1 && Count(database, "SELECT COUNT(*) FROM HistoricalProjection") == 1 &&
            Count(database, "SELECT COUNT(*) FROM PreferenceRevision") == 1, "source checks, conflicts, windows and preferences persist");

        var overrideValue = repository.AddOverride(new ObservationOverride("override-one", "round-1", "registration_end",
            "{\"date\":\"2026-09-21\"}", claim.Id, 1, "User checked the official notice", DateTimeOffset.UtcNow));
        Check(repository.GetActiveOverride("round-1", "registration_end").ValueJson == overrideValue.ValueJson,
            "active user override is visible separately from source claim");
        repository.RevokeOverride(overrideValue.Id, overrideValue.Revision);
        Throws<KeyNotFoundException>(() => repository.GetActiveOverride("round-1", "registration_end"));
        Throws<InvalidOperationException>(() => repository.SaveSnapshot(new SourceSnapshot("bad-hash", "university-site", null, null, null, "wrong",
            DateTimeOffset.UtcNow, null, null, "manual", "test"), firstBytes, "source-page", "text/plain"));
        Check(Count(database, "SELECT COUNT(*) FROM SourceSnapshot WHERE Id='bad-hash'") == 0,
            "hash mismatch is rejected before snapshot publication");
        var backupService = new BackupService(database);
        var backup = backupService.Create(Path.Combine(directory, "workspace-backup"));
        var verified = backupService.Verify(backup.DirectoryPath);
        Check(verified.WorkspaceId == new OrganizationRepository(database).Get().WorkspaceId &&
            verified.ArtifactCount >= 2 && verified.DataRevision > 0, "workspace backup validates database and referenced artifacts");
        File.AppendAllText(Path.Combine(backup.DirectoryPath, "database.db"), "corrupt");
        Throws<InvalidDataException>(() => backupService.Verify(backup.DirectoryPath));
        Console.WriteLine("PASS: immutable source snapshots, hash-addressed artifacts, claim supports, source revisions and reversible overrides");
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

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
