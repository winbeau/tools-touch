using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ToolsTouch.Application;
using ToolsTouch.Core;

namespace ToolsTouch.Infrastructure.Collection;

public sealed class FacultyIdentityImportService(LocalDatabase database) : IFacultyIdentityImportService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string DirectorySource = "official-faculty-directory";

    public Task<FacultyIdentityImportSummary> ImportAsync(string batchId,
        FacultyIdentityImportOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Import(batchId, options ?? new FacultyIdentityImportOptions(), cancellationToken));
    }

    private FacultyIdentityImportSummary Import(string batchId, FacultyIdentityImportOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchId);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var batch = ReadBatch(connection, transaction, batchId);
        if (batch.State is not "Completed") throw new InvalidOperationException("CRAWL_BATCH_NOT_FINISHED");
        if (!options.IncludePartialBatches && batch.Complete == 0) throw new InvalidOperationException("PARTIAL_CRAWL_NOT_ALLOWED");
        if (!batch.SourceKey.Equals(DirectorySource, StringComparison.Ordinal))
            throw new InvalidOperationException("FACULTY_SOURCE_UNSUPPORTED");

        var scope = JsonSerializer.Deserialize<FacultyDirectoryScope>(batch.ScopeJson, JsonOptions)
            ?? throw new InvalidOperationException("CRAWL_SCOPE_INVALID");
        var department = ReadDepartment(connection, transaction, scope.DepartmentId)
            ?? throw new InvalidOperationException("DIRECTORY_DEPARTMENT_NOT_FOUND");
        var items = ReadFetchedItems(connection, transaction, batchId);
        var issues = new List<FacultyIdentityImportIssue>();
        var imported = 0;
        var updated = 0;
        var unchanged = 0;
        var appointments = 0;
        var claims = 0;
        var ambiguous = 0;
        var changed = false;
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = JsonSerializer.Deserialize<FacultyPagePayload>(item.PayloadJson, JsonOptions)
                ?? throw new InvalidOperationException("FACULTY_PAGE_PAYLOAD_INVALID");
            foreach (var candidate in payload.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(candidate.ExternalId))
                {
                    issues.Add(new("", "FACULTY_EXTERNAL_ID_REQUIRED", "A directory candidate has no stable external ID.", []));
                    continue;
                }
                if (candidate.DepartmentName is not null &&
                    !candidate.DepartmentName.Equals(department.Name, StringComparison.OrdinalIgnoreCase))
                {
                    var resolutionId = StableId("faculty-resolution", batchId + "|" + payload.SnapshotId + "|" + candidate.ExternalId);
                    changed |= InsertResolution(connection, transaction, resolutionId, batchId, payload.SnapshotId,
                        batch.SourceKey, candidate, [], "DIRECTORY_DEPARTMENT_MISMATCH");
                    issues.Add(new(candidate.ExternalId, "DIRECTORY_DEPARTMENT_MISMATCH", "The entry is outside the requested department scope.", []));
                    ambiguous++;
                    continue;
                }

                var professorId = FindIdentity(connection, transaction, batch.SourceKey, candidate.ExternalId);
                var wasNew = professorId is null;
                if (professorId is not null && !ProfessorExists(connection, transaction, professorId))
                    professorId = null;
                var homepage = NormalizeHomepage(candidate.HomepageUrl);
                if (professorId is null && homepage is not null)
                {
                    var homepageMatches = FindByHomepage(connection, transaction, homepage);
                    if (homepageMatches.Count == 1) professorId = homepageMatches[0];
                    else if (homepageMatches.Count > 1)
                    {
                        changed |= QueueResolution(connection, transaction, batchId, batch.SourceKey, payload.SnapshotId, candidate,
                            homepageMatches, "HOMEPAGE_MATCH_AMBIGUOUS");
                        issues.Add(new(candidate.ExternalId, "HOMEPAGE_MATCH_AMBIGUOUS", "The homepage matches multiple professors.", homepageMatches));
                        ambiguous++;
                        continue;
                    }
                }
                if (professorId is null)
                {
                    var nameMatches = FindByNameAndInstitution(connection, transaction, candidate.Name, department.SchoolName);
                    if (nameMatches.Count == 1) professorId = nameMatches[0];
                    else if (nameMatches.Count > 1)
                    {
                        changed |= QueueResolution(connection, transaction, batchId, batch.SourceKey, payload.SnapshotId, candidate,
                            nameMatches, "NAME_INSTITUTION_AMBIGUOUS");
                        issues.Add(new(candidate.ExternalId, "NAME_INSTITUTION_AMBIGUOUS", "The name and institution match multiple professors.", nameMatches));
                        ambiguous++;
                        continue;
                    }
                }

                var email = NormalizePublicEmail(candidate.PublicEmail, candidate.PublicEmailRaw);
                if (professorId is not null && homepage is not null)
                {
                    var homepageOwners = FindByHomepage(connection, transaction, homepage)
                        .Where(id => !id.Equals(professorId, StringComparison.Ordinal)).ToArray();
                    if (homepageOwners.Length > 0)
                    {
                        changed |= QueueResolution(connection, transaction, batchId, batch.SourceKey, payload.SnapshotId, candidate,
                            homepageOwners, "HOMEPAGE_OWNERSHIP_CONFLICT");
                        issues.Add(new(candidate.ExternalId, "HOMEPAGE_OWNERSHIP_CONFLICT", "The migrated homepage is already assigned to another professor.", homepageOwners));
                        ambiguous++;
                        continue;
                    }
                }
                if (professorId is null)
                {
                    professorId = StableId("professor", batch.SourceKey + "|" + candidate.ExternalId);
                    changed |= InsertProfessor(connection, transaction, professorId, candidate.Name.Trim(), department.SchoolName,
                        homepage, email);
                    imported++;
                }
                else
                {
                    changed |= UpdateProfessor(connection, transaction, professorId, candidate.Name.Trim(), homepage, email,
                        out var profileChanged);
                    if (profileChanged) updated++;
                    else if (!wasNew) unchanged++;
                }

                changed |= InsertExternalIdentity(connection, transaction, batch.SourceKey, "Professor", candidate.ExternalId, professorId);
                var appointmentId = StableId("appointment", batch.SourceKey + "|" + candidate.ExternalId + "|" + department.Id);
                var claimId = StableId("claim", payload.SnapshotId + "|" + candidate.ExternalId + "|faculty_directory_entry");
                var claimValue = JsonSerializer.Serialize(new
                {
                    external_id = candidate.ExternalId,
                    name = candidate.Name,
                    homepage,
                    role = candidate.Role,
                    source_url = candidate.SourceUrl,
                    department = department.Name
                });
                var appointmentClaimInserted = InsertClaim(connection, transaction, claimId, payload.SnapshotId, "faculty_directory_entry", professorId,
                    claimValue, candidate.RawText, candidate.SourceUrl);
                changed |= appointmentClaimInserted;
                claims += appointmentClaimInserted ? 1 : 0;
                var appointmentInserted = InsertAppointment(connection, transaction, appointmentId, professorId, department.Id, candidate.Role, claimId);
                changed |= appointmentInserted;
                appointments += appointmentInserted ? 1 : 0;
                if (email is not null)
                {
                    var emailClaimId = StableId("claim", payload.SnapshotId + "|" + candidate.ExternalId + "|public_email|" + email);
                    var emailClaimInserted = InsertClaim(connection, transaction, emailClaimId, payload.SnapshotId, "public_email", professorId,
                        JsonSerializer.Serialize(new { email, raw = candidate.PublicEmailRaw ?? candidate.PublicEmail, normalization = "explicit_public_source" }),
                        candidate.PublicEmailRaw ?? email, candidate.SourceUrl);
                    changed |= emailClaimInserted;
                    claims += emailClaimInserted ? 1 : 0;
                }
            }
        }

        if (changed)
        {
            using var revision = LocalDatabase.Command(connection,
                "UPDATE WorkspaceMeta SET DataRevision=DataRevision+1 WHERE Id=1");
            revision.Transaction = transaction;
            if (revision.ExecuteNonQuery() != 1) throw new InvalidOperationException("WORKSPACE_META_MISSING");
        }
        transaction.Commit();
        return new FacultyIdentityImportSummary(batchId, imported, updated, unchanged, appointments, claims, ambiguous, issues,
            new OrganizationRepository(database).Get().DataRevision);
    }

    private static Batch ReadBatch(SqliteConnection connection, SqliteTransaction transaction, string batchId)
    {
        using var command = LocalDatabase.Command(connection,
            "SELECT SourceKey,ScopeJson,State,Complete FROM CrawlBatch WHERE Id=$id", ("$id", batchId));
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("CRAWL_BATCH_NOT_FOUND");
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3));
    }

    private static DepartmentInfo? ReadDepartment(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = LocalDatabase.Command(connection, """
            SELECT d.Id,d.CanonicalName,s.CanonicalName
            FROM Department d JOIN School s ON s.Id=d.SchoolId
            WHERE d.Id=$id AND d.ArchivedAt IS NULL
            """, ("$id", id));
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    private static IReadOnlyList<CrawlItem> ReadFetchedItems(SqliteConnection connection, SqliteTransaction transaction, string batchId)
    {
        using var command = LocalDatabase.Command(connection,
            "SELECT Id,PayloadJson FROM CrawlItem WHERE BatchId=$batch AND State IN ('Fetched','Selected') ORDER BY Id", ("$batch", batchId));
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        var result = new List<CrawlItem>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    private static string? FindIdentity(SqliteConnection connection, SqliteTransaction transaction, string sourceKey, string externalId)
    {
        using var command = LocalDatabase.Command(connection,
            "SELECT EntityId FROM ExternalIdentity WHERE SourceKey=$source AND EntityKind='Professor' AND ExternalId=$external",
            ("$source", sourceKey), ("$external", externalId));
        command.Transaction = transaction;
        return command.ExecuteScalar() as string;
    }

    private static bool ProfessorExists(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = LocalDatabase.Command(connection, "SELECT COUNT(*) FROM Professor WHERE Id=$id", ("$id", id));
        command.Transaction = transaction;
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    private static List<string> FindByHomepage(SqliteConnection connection, SqliteTransaction transaction, string homepage)
    {
        using var command = LocalDatabase.Command(connection, "SELECT Id FROM Professor WHERE Homepage=$homepage ORDER BY Id", ("$homepage", homepage));
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    private static List<string> FindByNameAndInstitution(SqliteConnection connection, SqliteTransaction transaction, string name, string institution)
    {
        using var command = LocalDatabase.Command(connection,
            "SELECT Id FROM Professor WHERE Name=$name AND Institution=$institution ORDER BY Id", ("$name", name.Trim()), ("$institution", institution));
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    private static bool InsertProfessor(SqliteConnection connection, SqliteTransaction transaction, string id, string name,
        string institution, string? homepage, string? email)
    {
        using var command = LocalDatabase.Command(connection, """
            INSERT OR IGNORE INTO Professor(Id,Name,Institution,Homepage,Email,EvidenceJson,UpdatedAt)
            VALUES($id,$name,$institution,$homepage,$email,'[]',$now)
            """, ("$id", id), ("$name", name), ("$institution", institution), ("$homepage", homepage), ("$email", email),
            ("$now", DateTimeOffset.UtcNow.ToString("O")));
        command.Transaction = transaction;
        return command.ExecuteNonQuery() == 1;
    }

    private static bool UpdateProfessor(SqliteConnection connection, SqliteTransaction transaction, string id, string name,
        string? homepage, string? email, out bool profileChanged)
    {
        using var current = LocalDatabase.Command(connection, "SELECT Name,Homepage,Email FROM Professor WHERE Id=$id", ("$id", id));
        current.Transaction = transaction;
        using var reader = current.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("PROFESSOR_NOT_FOUND");
        var oldName = reader.GetString(0);
        var oldHomepage = reader.IsDBNull(1) ? null : reader.GetString(1);
        var oldEmail = reader.IsDBNull(2) ? null : reader.GetString(2);
        reader.Close();
        var newHomepage = homepage ?? oldHomepage;
        var newEmail = email ?? oldEmail;
        profileChanged = oldName != name || oldHomepage != newHomepage || oldEmail != newEmail;
        if (!profileChanged) return false;
        using var update = LocalDatabase.Command(connection, """
            UPDATE Professor SET Name=$name,Homepage=$homepage,Email=$email,UpdatedAt=$now WHERE Id=$id
            """, ("$name", name), ("$homepage", newHomepage), ("$email", newEmail),
            ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$id", id));
        update.Transaction = transaction;
        return update.ExecuteNonQuery() == 1;
    }

    private static bool InsertExternalIdentity(SqliteConnection connection, SqliteTransaction transaction,
        string sourceKey, string entityKind, string externalId, string entityId)
    {
        using var command = LocalDatabase.Command(connection, """
            INSERT OR IGNORE INTO ExternalIdentity(SourceKey,EntityKind,ExternalId,EntityId,CreatedAt)
            VALUES($source,$kind,$external,$entity,$now)
            """, ("$source", sourceKey), ("$kind", entityKind), ("$external", externalId), ("$entity", entityId),
            ("$now", DateTimeOffset.UtcNow.ToString("O")));
        command.Transaction = transaction;
        try { return command.ExecuteNonQuery() == 1; }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("FACULTY_EXTERNAL_ID_CONFLICT", error);
        }
    }

    private static bool InsertAppointment(SqliteConnection connection, SqliteTransaction transaction, string id,
        string professorId, string departmentId, string? role, string claimId)
    {
        using var command = LocalDatabase.Command(connection, """
            INSERT OR IGNORE INTO Appointment(Id,ProfessorId,DepartmentId,Role,IsPrimary,EvidenceClaimId,Revision,CreatedAt,UpdatedAt)
            VALUES($id,$professor,$department,$role,0,$claim,1,$now,$now)
            """, ("$id", id), ("$professor", professorId), ("$department", departmentId), ("$role", role),
            ("$claim", claimId), ("$now", DateTimeOffset.UtcNow.ToString("O")));
        command.Transaction = transaction;
        if (command.ExecuteNonQuery() == 1) return true;
        using var update = LocalDatabase.Command(connection, """
            UPDATE Appointment SET Role=$role,EvidenceClaimId=$claim,Revision=Revision+1,UpdatedAt=$now
            WHERE Id=$id AND NOT (Role IS $role AND EvidenceClaimId IS $claim)
            """, ("$role", role), ("$claim", claimId), ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$id", id));
        update.Transaction = transaction;
        return update.ExecuteNonQuery() == 1;
    }

    private static bool InsertClaim(SqliteConnection connection, SqliteTransaction transaction, string id, string snapshotId,
        string claimType, string professorId, string valueJson, string quotedText, string sourceUrl)
    {
        using var command = LocalDatabase.Command(connection, """
            INSERT OR IGNORE INTO EvidenceClaim(Id,SnapshotId,SubjectKind,SubjectId,ClaimType,ValueJson,QuotedText,LocatorJson,OriginKind,ConfidenceLabel)
            VALUES($id,$snapshot,'Professor',$professor,$type,$value,$quoted,$locator,'OfficialFact','High')
            """, ("$id", id), ("$snapshot", snapshotId), ("$professor", professorId), ("$type", claimType),
            ("$value", valueJson), ("$quoted", quotedText), ("$locator", JsonSerializer.Serialize(new { url = sourceUrl })));
        command.Transaction = transaction;
        return command.ExecuteNonQuery() == 1;
    }

    private static bool QueueResolution(SqliteConnection connection, SqliteTransaction transaction, string batchId, string sourceKey,
        string snapshotId, FacultyDirectoryCandidate candidate, IReadOnlyList<string> candidateIds, string reason) =>
        InsertResolution(connection, transaction, StableId("faculty-resolution", batchId + "|" + snapshotId + "|" + candidate.ExternalId),
            batchId, snapshotId, sourceKey, candidate, candidateIds, reason);

    private static bool InsertResolution(SqliteConnection connection, SqliteTransaction transaction, string id, string batchId,
        string snapshotId, string sourceKey, FacultyDirectoryCandidate candidate, IReadOnlyList<string> candidateIds, string reason)
    {
        using var command = LocalDatabase.Command(connection, """
            INSERT OR IGNORE INTO FacultyIdentityResolution(Id,CrawlBatchId,SourceSnapshotId,SourceKey,ExternalId,RawName,RawHomepage,CandidateIdsJson,State,Reason,CreatedAt)
            VALUES($id,$batch,$snapshot,$source,$external,$name,$homepage,$candidates,'Pending',$reason,$now)
            """, ("$id", id), ("$batch", batchId), ("$snapshot", snapshotId), ("$source", sourceKey),
            ("$external", candidate.ExternalId), ("$name", candidate.Name), ("$homepage", candidate.HomepageUrl),
            ("$candidates", JsonSerializer.Serialize(candidateIds)), ("$reason", reason),
            ("$now", DateTimeOffset.UtcNow.ToString("O")));
        command.Transaction = transaction;
        return command.ExecuteNonQuery() == 1;
    }

    private static string? NormalizeHomepage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try
        {
            var uri = PublicWeb.ValidateUri(value);
            return new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri.TrimEnd('/');
        }
        catch (InvalidOperationException) { return null; }
    }

    private static string? NormalizePublicEmail(string? value, string? raw)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? raw : value;
        if (candidate is null) return null;
        candidate = candidate.Trim();
        if (candidate.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) candidate = candidate[7..].Split('?', 2)[0];
        candidate = candidate.Replace("[at]", "@", StringComparison.OrdinalIgnoreCase)
            .Replace("(at)", "@", StringComparison.OrdinalIgnoreCase)
            .Replace("[dot]", ".", StringComparison.OrdinalIgnoreCase)
            .Replace("(dot)", ".", StringComparison.OrdinalIgnoreCase)
            .Replace(" ", "", StringComparison.Ordinal);
        if (candidate.Contains('\r') || candidate.Contains('\n') || !MailAddress.TryCreate(candidate, out var address) ||
            address.Address != candidate) return null;
        return address.Address;
    }

    private static string StableId(string prefix, string value) => prefix + "-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..32];

    private sealed record Batch(string SourceKey, string ScopeJson, string State, int Complete);
    private sealed record DepartmentInfo(string Id, string Name, string SchoolName);
    private sealed record CrawlItem(string Id, string PayloadJson);
}
