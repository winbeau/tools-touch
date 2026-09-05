using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using ToolsTouch.Application;
using ToolsTouch.Core;

namespace ToolsTouch.Infrastructure.Collection;

public sealed class AdmissionImportService(LocalDatabase database, ArtifactStore artifacts) : IAdmissionImportService
{
    private const string SourceKey = "baoyan";
    private const string ParseVersion = "baoyan-manifest-v1";
    private static readonly JsonSerializerOptions JsonOptions = new();

    public Task<AdmissionImportSummary> ImportAsync(CollectorResult result, AdmissionImportOptions options,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Import(result, options, cancellationToken));
    }

    private AdmissionImportSummary Import(CollectorResult result, AdmissionImportOptions options, CancellationToken cancellationToken)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));
        if (options.TargetCycleYear is < 1900 or > 2200) throw new ArgumentOutOfRangeException(nameof(options.TargetCycleYear));
        if (string.IsNullOrWhiteSpace(options.TimeZoneId)) throw new ArgumentException("TIME_ZONE_REQUIRED", nameof(options));

        var limits = new CollectorLimits(
            MaxRecords: Math.Max(100_000, result.Manifest.Counts.Selected),
            MaxBytes: Math.Max(100L * 1024 * 1024, result.Manifest.Files.Sum(file => file.ByteLength) + 1));
        var manifest = CollectorManifestValidator.ReadAndValidate(result.StagingDirectory, limits);
        var records = ReadRecords(result.StagingDirectory);
        var importId = StableId("import", SourceKey + "|" + manifest.FetchedAt.ToUniversalTime().ToString("O") + "|" + manifest.AdapterVersion);
        var issues = new List<AdmissionImportIssue>();
        var rawArtifacts = StageRawFiles(result.StagingDirectory, manifest.Files);
        var imported = 0;
        var updated = 0;
        var unchanged = 0;
        var ambiguous = 0;
        var skipped = 0;
        var changed = false;

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(record.School.Trim(), ScopeSchool(manifest.Scope), StringComparison.Ordinal))
            {
                var mismatchArtifact = rawArtifacts[record.RawRef.Path];
                var mismatchSnapshotId = StableId("snapshot", SourceKey + "|" + record.ExternalId + "|" + mismatchArtifact.ContentHash);
                var mismatchSnapshot = new SourceSnapshot(mismatchSnapshotId, SourceKey, record.SourceUrl, record.SourceUrl, record.ExternalId,
                    mismatchArtifact.ContentHash, manifest.FetchedAt, null, NormalizeYear(record.SourceYear), "Http", ParseVersion,
                    mismatchArtifact.Id, record.Title);
                changed |= InsertArtifact(connection, transaction, mismatchArtifact);
                changed |= InsertSnapshot(connection, transaction, mismatchSnapshot);
                changed |= AddResolution(connection, transaction, importId + "|SCHOOL_SCOPE_MISMATCH", record.ExternalId, mismatchSnapshot.Id,
                    record.School, record.Department, []);
                issues.Add(new(record.ExternalId, "SCHOOL_SCOPE_MISMATCH", "Record school does not match the requested scope."));
                skipped++;
                continue;
            }

            var rawArtifact = rawArtifacts[record.RawRef.Path];
            var snapshotId = StableId("snapshot", SourceKey + "|" + record.ExternalId + "|" + rawArtifact.ContentHash);
            var hadSnapshot = ScalarString(connection, transaction,
                "SELECT Id FROM SourceSnapshot WHERE SourceKey=$source AND ExternalRecordId=$external AND ContentHash<>$hash LIMIT 1",
                ("$source", SourceKey), ("$external", record.ExternalId), ("$hash", rawArtifact.ContentHash)) is not null;
            var snapshot = new SourceSnapshot(snapshotId, SourceKey, record.SourceUrl, record.SourceUrl, record.ExternalId,
                rawArtifact.ContentHash, manifest.FetchedAt, null, NormalizeYear(record.SourceYear), "Http", ParseVersion,
                rawArtifact.Id, record.Title);
            var artifactChanged = InsertArtifact(connection, transaction, rawArtifact);
            var snapshotChanged = InsertSnapshot(connection, transaction, snapshot);
            changed |= artifactChanged || snapshotChanged;

            var school = ResolveSchool(connection, transaction, record.School, record.ExternalId, snapshot.Id, issues, importId,
                out var schoolAmbiguous, out var schoolResolutionChanged);
            if (school is null)
            {
                if (schoolAmbiguous) ambiguous++; else skipped++;
                changed |= schoolResolutionChanged;
                continue;
            }

            if (string.IsNullOrWhiteSpace(record.Department))
            {
                changed |= AddResolution(connection, transaction, importId + "|DEPARTMENT_REQUIRED", record.ExternalId, snapshot.Id,
                    record.School, record.Department, []);
                issues.Add(new(record.ExternalId, "DEPARTMENT_REQUIRED", "The source record has no department; it was retained for review."));
                skipped++;
                continue;
            }

            var department = ResolveDepartment(connection, transaction, school, record.Department!, record.ExternalId, snapshot.Id,
                issues, importId, out var departmentAmbiguous, out var departmentResolutionChanged);
            if (department is null)
            {
                if (departmentAmbiguous) ambiguous++; else skipped++;
                changed |= departmentResolutionChanged;
                continue;
            }

            var cycle = CycleResolver.Resolve(options.TargetCycleYear, NormalizeYear(record.SourceYear), record.Title);
            var kinds = SplitKinds(record.Kind);
            var recordChanged = snapshotChanged;
            foreach (var kind in kinds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var program = ResolveProgram(connection, transaction, department.Id, kind, record.ExternalId, out var programChanged);
                changed |= programChanged;
                var round = ResolveRound(connection, transaction, program, cycle, kind, record, out var roundChanged);
                changed |= roundChanged;
                var observation = BuildObservation(round, snapshot, cycle, record, options.TimeZoneId);
                var observationChanged = InsertObservation(connection, transaction, observation);
                changed |= observationChanged;
                recordChanged |= observationChanged || roundChanged || programChanged;
            }

            if (recordChanged)
            {
                imported++;
                if (!InsertExternalIdentityIfMissing(connection, transaction, SourceKey, "School", record.School, school.Id)) { }
                if (hadSnapshot) updated++;
            }
            else unchanged++;
        }

        if (changed)
        {
            using var revision = LocalDatabase.Command(connection, "UPDATE WorkspaceMeta SET DataRevision=DataRevision+1 WHERE Id=1");
            revision.Transaction = transaction;
            if (revision.ExecuteNonQuery() != 1) throw new InvalidOperationException("WORKSPACE_META_MISSING");
        }
        transaction.Commit();
        var dataRevision = new OrganizationRepository(database).Get().DataRevision;
        return new(importId, manifest.Source, manifest.Counts.Scanned, manifest.Counts.Selected, imported, updated, unchanged,
            ambiguous, skipped, issues, dataRevision);
    }

    private Dictionary<string, Artifact> StageRawFiles(string stagingDirectory, IReadOnlyList<CollectorFile> files)
    {
        var result = new Dictionary<string, Artifact>(StringComparer.Ordinal);
        foreach (var file in files.Where(file => file.Kind == "raw"))
        {
            var path = Path.Combine(Path.GetFullPath(stagingDirectory), file.Path.Replace('/', Path.DirectorySeparatorChar));
            result[file.Path] = artifacts.Stage("collector-raw", File.ReadAllBytes(path), "application/json", ".json");
        }
        return result;
    }

    private static IReadOnlyList<SourceRecord> ReadRecords(string stagingDirectory)
    {
        var path = Path.Combine(Path.GetFullPath(stagingDirectory), "records.jsonl");
        var result = new List<SourceRecord>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        while (reader.ReadLine() is { } line)
        {
            var record = JsonSerializer.Deserialize<SourceRecord>(line, JsonOptions)
                ?? throw new InvalidOperationException("INVALID_COLLECTOR_RECORD_JSON");
            result.Add(record);
        }
        return result;
    }

    private static School? ResolveSchool(SqliteConnection connection, SqliteTransaction transaction, string name, string externalId,
        string snapshotId, List<AdmissionImportIssue> issues, string importId, out bool ambiguous, out bool resolutionChanged)
    {
        ambiguous = false;
        resolutionChanged = false;
        var identity = FindIdentity(connection, transaction, "School", name);
        if (identity is not null) return ReadSchool(connection, transaction, identity);
        var aliases = FindAliases(connection, transaction, "School", name, null);
        if (aliases.Count > 1)
        {
            ambiguous = true;
            resolutionChanged = AddResolution(connection, transaction, importId, externalId, snapshotId, name, null, aliases);
            issues.Add(new(externalId, "SCHOOL_AMBIGUOUS", "The school name matches multiple aliases.", aliases));
            return null;
        }
        if (aliases.Count == 1)
        {
            InsertExternalIdentityIfMissing(connection, transaction, SourceKey, "School", name, aliases[0]);
            return ReadSchool(connection, transaction, aliases[0]);
        }
        var matches = FindSchools(connection, transaction, name);
        if (matches.Count > 1)
        {
            ambiguous = true;
            resolutionChanged = AddResolution(connection, transaction, importId, externalId, snapshotId, name, null, matches);
            issues.Add(new(externalId, "SCHOOL_AMBIGUOUS", "The school name matches multiple entities.", matches));
            return null;
        }
        if (matches.Count == 1)
        {
            AddAlias(connection, transaction, "School", matches[0], name, SourceKey);
            InsertExternalIdentityIfMissing(connection, transaction, SourceKey, "School", name, matches[0]);
            return ReadSchool(connection, transaction, matches[0]);
        }

        var id = StableId("school", SourceKey + "|" + name);
        using var insert = LocalDatabase.Command(connection, """
            INSERT OR IGNORE INTO School(Id,CanonicalName,ShortName,Revision,CreatedAt,UpdatedAt)
            VALUES($id,$name,$short,1,$now,$now)
            """, ("$id", id), ("$name", name.Trim()), ("$short", name.Trim()), ("$now", Now));
        insert.Transaction = transaction;
        insert.ExecuteNonQuery();
        AddAlias(connection, transaction, "School", id, name, SourceKey);
        InsertExternalIdentityIfMissing(connection, transaction, SourceKey, "School", name, id);
        return ReadSchool(connection, transaction, id);
    }

    private static Department? ResolveDepartment(SqliteConnection connection, SqliteTransaction transaction, School school, string name,
        string externalId, string snapshotId, List<AdmissionImportIssue> issues, string importId, out bool ambiguous, out bool resolutionChanged)
    {
        ambiguous = false;
        resolutionChanged = false;
        var aliases = FindAliases(connection, transaction, "Department", name, school.Id);
        if (aliases.Count > 1)
        {
            ambiguous = true;
            resolutionChanged = AddResolution(connection, transaction, importId, externalId, snapshotId, school.CanonicalName, name, aliases);
            issues.Add(new(externalId, "DEPARTMENT_AMBIGUOUS", "The department name matches multiple aliases.", aliases));
            return null;
        }
        if (aliases.Count == 1) return ReadDepartment(connection, transaction, aliases[0]);
        var matches = FindDepartments(connection, transaction, school.Id, name);
        if (matches.Count > 1)
        {
            ambiguous = true;
            resolutionChanged = AddResolution(connection, transaction, importId, externalId, snapshotId, school.CanonicalName, name, matches);
            issues.Add(new(externalId, "DEPARTMENT_AMBIGUOUS", "The department name matches multiple entities.", matches));
            return null;
        }
        if (matches.Count == 1)
        {
            AddAlias(connection, transaction, "Department", matches[0], name, SourceKey);
            return ReadDepartment(connection, transaction, matches[0]);
        }

        var id = StableId("department", school.Id + "|" + name);
        using var insert = LocalDatabase.Command(connection, """
            INSERT OR IGNORE INTO Department(Id,SchoolId,CanonicalName,Kind,Revision,CreatedAt,UpdatedAt)
            VALUES($id,$school,$name,'Imported',1,$now,$now)
            """, ("$id", id), ("$school", school.Id), ("$name", name.Trim()), ("$now", Now));
        insert.Transaction = transaction;
        insert.ExecuteNonQuery();
        AddAlias(connection, transaction, "Department", id, name, SourceKey);
        InsertExternalIdentityIfMissing(connection, transaction, SourceKey, "Department", school.Id + "|" + name, id);
        return ReadDepartment(connection, transaction, id);
    }

    private static AdmissionProgram ResolveProgram(SqliteConnection connection, SqliteTransaction transaction, string departmentId, string kind,
        string externalId, out bool changed)
    {
        var identityKey = departmentId + "|" + kind;
        var existing = FindIdentity(connection, transaction, "AdmissionProgram", identityKey);
        if (existing is not null)
        {
            changed = false;
            return ReadProgram(connection, transaction, existing);
        }
        var id = StableId("program", identityKey);
        using var insert = LocalDatabase.Command(connection, """
            INSERT OR IGNORE INTO AdmissionProgram(Id,DepartmentId,Name,DegreeType,Track,Revision,CreatedAt,UpdatedAt)
            VALUES($id,$department,'来源公告（未细分项目）','Unknown',$track,1,$now,$now)
            """, ("$id", id), ("$department", departmentId), ("$track", kind), ("$now", Now));
        insert.Transaction = transaction;
        changed = insert.ExecuteNonQuery() == 1;
        changed |= InsertExternalIdentityIfMissing(connection, transaction, SourceKey, "AdmissionProgram", identityKey, id);
        return ReadProgram(connection, transaction, id);
    }

    private static AdmissionRound ResolveRound(SqliteConnection connection, SqliteTransaction transaction, AdmissionProgram program,
        CycleResolution cycle, string kind, SourceRecord record, out bool changed)
    {
        var identityKey = record.ExternalId + "|" + kind;
        var existing = FindIdentity(connection, transaction, "AdmissionRound", identityKey);
        var roundKind = Enum.Parse<AdmissionRoundKind>(kind);
        var id = existing ?? StableId("round", SourceKey + "|" + identityKey);
        var round = existing is null ? null : ReadRound(connection, transaction, existing);
        if (round is null)
        {
            round = new AdmissionRound(id, program.Id, cycle.CycleYear, cycle.EntryYear, roundKind, identityKey, record.Title,
                record.OfficialUrl, record.ApplicationUrl);
            using var insert = LocalDatabase.Command(connection, """
                INSERT OR IGNORE INTO AdmissionRound(Id,ProgramId,CycleYear,EntryYear,Kind,RoundKey,Title,OfficialUrl,ApplicationUrl,Revision,CreatedAt,UpdatedAt)
                VALUES($id,$program,$cycle,$entry,$kind,$key,$title,$official,$application,1,$now,$now)
                """, ("$id", round.Id), ("$program", round.ProgramId), ("$cycle", round.CycleYear), ("$entry", round.EntryYear),
                ("$kind", kind), ("$key", round.RoundKey), ("$title", round.Title), ("$official", round.OfficialUrl),
                ("$application", round.ApplicationUrl), ("$now", Now));
            insert.Transaction = transaction;
            changed = insert.ExecuteNonQuery() == 1;
            changed |= InsertExternalIdentityIfMissing(connection, transaction, SourceKey, "AdmissionRound", identityKey, id);
            return ReadRound(connection, transaction, id);
        }

        changed = false;
        if (round.Title != record.Title || round.OfficialUrl != record.OfficialUrl || round.ApplicationUrl != record.ApplicationUrl)
        {
            using var update = LocalDatabase.Command(connection, """
                UPDATE AdmissionRound SET Title=$title,OfficialUrl=$official,ApplicationUrl=$application,Revision=Revision+1,UpdatedAt=$now WHERE Id=$id
                """, ("$id", id), ("$title", record.Title), ("$official", record.OfficialUrl), ("$application", record.ApplicationUrl), ("$now", Now));
            update.Transaction = transaction;
            changed = update.ExecuteNonQuery() == 1;
        }
        return ReadRound(connection, transaction, id);
    }

    private static WindowObservation BuildObservation(AdmissionRound round, SourceSnapshot snapshot, CycleResolution cycle,
        SourceRecord record, string timeZoneId)
    {
        var precision = DeterminePrecision(record.RegistrationStartRaw, record.RegistrationEndRaw);
        var startDate = precision == DatePrecision.Date ? DateValue(record.RegistrationStartRaw) : null;
        var endDate = precision == DatePrecision.Date ? DateValue(record.RegistrationEndRaw) : null;
        var startInstant = precision == DatePrecision.Instant ? record.RegistrationStartRaw : null;
        var endInstant = precision == DatePrecision.Instant ? record.RegistrationEndRaw : null;
        var observationId = StableId("observation", round.Id + "|" + snapshot.Id);
        return new WindowObservation(observationId, round.Id, snapshot.Id, cycle.SourceYear, cycle.CycleYear, cycle.EntryYear,
            cycle.CycleYear is null ? "Unknown" : "SourceYear", precision == DatePrecision.Unknown ? "RawUnparsed" : "Raw",
            record.RegistrationStartRaw, record.RegistrationEndRaw, record.EventStartRaw, record.EventEndRaw,
            startDate?.ToString("yyyy-MM-dd"), endDate?.ToString("yyyy-MM-dd"), null, null, startInstant, endInstant, null, null, precision, timeZoneId, snapshot.FetchedAt);
    }

    private static bool InsertObservation(SqliteConnection connection, SqliteTransaction transaction, WindowObservation observation)
    {
        var previous = ScalarString(connection, transaction, "SELECT Id FROM WindowObservation WHERE Id=$id", ("$id", observation.Id));
        if (previous is not null) return false;
        var supersedes = ScalarString(connection, transaction,
            "SELECT Id FROM WindowObservation WHERE RoundId=$round ORDER BY ObservedAt DESC,Id DESC LIMIT 1", ("$round", observation.RoundId));
        using var insert = LocalDatabase.Command(connection, """
            INSERT INTO WindowObservation(
                Id,RoundId,SourceSnapshotId,SourceYear,CycleYear,EntryYear,YearBasis,DateBasis,
                RegistrationStartRaw,RegistrationEndRaw,EventStartRaw,EventEndRaw,
                RegistrationStartLocalDate,RegistrationEndLocalDate,EventStartLocalDate,EventEndLocalDate,
                RegistrationStartInstant,RegistrationEndInstant,EventStartInstant,EventEndInstant,
                Precision,TimeZone,ObservedAt,SupersedesId)
            VALUES($id,$round,$snapshot,$sourceYear,$cycle,$entry,$yearBasis,$dateBasis,
                $registrationStartRaw,$registrationEndRaw,$eventStartRaw,$eventEndRaw,
                $registrationStartDate,$registrationEndDate,$eventStartDate,$eventEndDate,
                $registrationStartInstant,$registrationEndInstant,$eventStartInstant,$eventEndInstant,
                $precision,$timeZone,$observed,$supersedes)
            """, ("$id", observation.Id), ("$round", observation.RoundId), ("$snapshot", observation.SourceSnapshotId),
            ("$sourceYear", observation.SourceYear), ("$cycle", observation.CycleYear), ("$entry", observation.EntryYear),
            ("$yearBasis", observation.YearBasis), ("$dateBasis", observation.DateBasis),
            ("$registrationStartRaw", observation.RegistrationStartRaw), ("$registrationEndRaw", observation.RegistrationEndRaw),
            ("$eventStartRaw", observation.EventStartRaw), ("$eventEndRaw", observation.EventEndRaw),
            ("$registrationStartDate", observation.RegistrationStartLocalDate), ("$registrationEndDate", observation.RegistrationEndLocalDate),
            ("$eventStartDate", observation.EventStartLocalDate), ("$eventEndDate", observation.EventEndLocalDate),
            ("$registrationStartInstant", observation.RegistrationStartInstant), ("$registrationEndInstant", observation.RegistrationEndInstant),
            ("$eventStartInstant", observation.EventStartInstant), ("$eventEndInstant", observation.EventEndInstant),
            ("$precision", observation.Precision.ToString()), ("$timeZone", observation.TimeZone),
            ("$observed", observation.ObservedAt.ToString("O")), ("$supersedes", supersedes));
        insert.Transaction = transaction;
        insert.ExecuteNonQuery();
        return true;
    }

    private static bool InsertArtifact(SqliteConnection connection, SqliteTransaction transaction, Artifact artifact)
    {
        using var insert = LocalDatabase.Command(connection, """
            INSERT OR IGNORE INTO Artifact(Id,Kind,RelativePath,ContentHash,ByteLength,MimeType,CreatedAt)
            VALUES($id,$kind,$path,$hash,$length,$mime,$created)
            """, ("$id", artifact.Id), ("$kind", artifact.Kind), ("$path", artifact.RelativePath),
            ("$hash", artifact.ContentHash), ("$length", artifact.ByteLength), ("$mime", artifact.MimeType), ("$created", artifact.CreatedAt.ToString("O")));
        insert.Transaction = transaction;
        return insert.ExecuteNonQuery() == 1;
    }

    private static bool InsertSnapshot(SqliteConnection connection, SqliteTransaction transaction, SourceSnapshot snapshot)
    {
        using var insert = LocalDatabase.Command(connection, """
            INSERT OR IGNORE INTO SourceSnapshot(Id,SourceKey,OriginalUrl,CanonicalUrl,ExternalRecordId,ContentHash,FetchedAt,PublishedAt,SourceYear,Transport,ArtifactId,ParseVersion,Title)
            VALUES($id,$source,$original,$canonical,$external,$hash,$fetched,$published,$year,$transport,$artifact,$parse,$title)
            """, ("$id", snapshot.Id), ("$source", snapshot.SourceKey), ("$original", snapshot.OriginalUrl),
            ("$canonical", snapshot.CanonicalUrl), ("$external", snapshot.ExternalRecordId), ("$hash", snapshot.ContentHash),
            ("$fetched", snapshot.FetchedAt.ToString("O")), ("$published", snapshot.PublishedAt?.ToString("O")),
            ("$year", snapshot.SourceYear), ("$transport", snapshot.Transport), ("$artifact", snapshot.ArtifactId),
            ("$parse", snapshot.ParseVersion), ("$title", snapshot.Title));
        insert.Transaction = transaction;
        return insert.ExecuteNonQuery() == 1;
    }

    private static bool InsertExternalIdentityIfMissing(SqliteConnection connection, SqliteTransaction transaction,
        string source, string kind, string externalId, string entityId)
    {
        using var insert = LocalDatabase.Command(connection, """
            INSERT OR IGNORE INTO ExternalIdentity(SourceKey,EntityKind,ExternalId,EntityId,CreatedAt)
            VALUES($source,$kind,$external,$entity,$now)
            """, ("$source", source), ("$kind", kind), ("$external", externalId), ("$entity", entityId), ("$now", Now));
        insert.Transaction = transaction;
        return insert.ExecuteNonQuery() == 1;
    }

    private static void AddAlias(SqliteConnection connection, SqliteTransaction transaction, string kind, string entityId, string alias, string source)
    {
        using var insert = LocalDatabase.Command(connection, """
            INSERT OR IGNORE INTO EntityAlias(Id,EntityKind,EntityId,Alias,SourceKey)
            VALUES($id,$kind,$entity,$alias,$source)
            """, ("$id", StableId("alias", kind + "|" + entityId + "|" + source + "|" + alias.Trim().ToLowerInvariant())),
            ("$kind", kind), ("$entity", entityId), ("$alias", alias.Trim()), ("$source", source));
        insert.Transaction = transaction;
        insert.ExecuteNonQuery();
    }

    private static bool AddResolution(SqliteConnection connection, SqliteTransaction transaction, string importId, string externalId,
        string snapshotId, string school, string? department, IReadOnlyList<string> candidates)
    {
        using var insert = LocalDatabase.Command(connection, """
            INSERT OR IGNORE INTO EntityResolution(Id,SourceSnapshotId,RawSchool,RawDepartment,CandidateIdsJson,State,CreatedAt)
            VALUES($id,$snapshot,$school,$department,$candidates,'Pending',$now)
            """, ("$id", StableId("resolution", importId + "|" + externalId)), ("$snapshot", snapshotId),
            ("$school", school), ("$department", department), ("$candidates", JsonSerializer.Serialize(candidates)), ("$now", Now));
        insert.Transaction = transaction;
        return insert.ExecuteNonQuery() == 1;
    }

    private static List<string> FindSchools(SqliteConnection connection, SqliteTransaction transaction, string name) =>
        FindIds(connection, transaction, "SELECT Id FROM School WHERE ArchivedAt IS NULL AND CanonicalName COLLATE NOCASE=$name", ("$name", name.Trim()));

    private static List<string> FindDepartments(SqliteConnection connection, SqliteTransaction transaction, string schoolId, string name) =>
        FindIds(connection, transaction, "SELECT Id FROM Department WHERE ArchivedAt IS NULL AND SchoolId=$school AND CanonicalName COLLATE NOCASE=$name",
            ("$school", schoolId), ("$name", name.Trim()));

    private static List<string> FindAliases(SqliteConnection connection, SqliteTransaction transaction, string kind, string alias, string? schoolId)
    {
        var sql = schoolId is null
            ? "SELECT EntityId FROM EntityAlias WHERE EntityKind=$kind AND Alias COLLATE NOCASE=$alias"
            : "SELECT EntityAlias.EntityId FROM EntityAlias JOIN Department ON Department.Id=EntityAlias.EntityId WHERE EntityAlias.EntityKind=$kind AND EntityAlias.Alias COLLATE NOCASE=$alias AND Department.SchoolId=$school";
        return schoolId is null
            ? FindIds(connection, transaction, sql, ("$kind", kind), ("$alias", alias.Trim()))
            : FindIds(connection, transaction, sql, ("$kind", kind), ("$alias", alias.Trim()), ("$school", schoolId));
    }

    private static List<string> FindIds(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = LocalDatabase.Command(connection, sql, parameters);
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) if (!result.Contains(reader.GetString(0), StringComparer.Ordinal)) result.Add(reader.GetString(0));
        return result;
    }

    private static string? FindIdentity(SqliteConnection connection, SqliteTransaction transaction, string kind, string externalId) =>
        ScalarString(connection, transaction, "SELECT EntityId FROM ExternalIdentity WHERE SourceKey=$source AND EntityKind=$kind AND ExternalId=$external",
            ("$source", SourceKey), ("$kind", kind), ("$external", externalId));

    private static string? ScalarString(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = LocalDatabase.Command(connection, sql, parameters);
        command.Transaction = transaction;
        return command.ExecuteScalar() as string;
    }

    private static School ReadSchool(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = LocalDatabase.Command(connection, "SELECT Id,CanonicalName,ShortName,OfficialCode,Campus,City,Revision FROM School WHERE Id=$id", ("$id", id));
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("SCHOOL_NOT_FOUND");
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), NullableString(reader, 3), NullableString(reader, 4), NullableString(reader, 5), reader.GetInt64(6));
    }

    private static Department ReadDepartment(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = LocalDatabase.Command(connection, "SELECT Id,SchoolId,CanonicalName,OfficialCode,ParentDepartmentId,Kind,Revision FROM Department WHERE Id=$id", ("$id", id));
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("DEPARTMENT_NOT_FOUND");
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(5), NullableString(reader, 3), NullableString(reader, 4), reader.GetInt64(6));
    }

    private static AdmissionProgram ReadProgram(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = LocalDatabase.Command(connection, "SELECT Id,DepartmentId,Name,DegreeType,DisciplineCode,Track,Campus,Revision FROM AdmissionProgram WHERE Id=$id", ("$id", id));
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("PROGRAM_NOT_FOUND");
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), NullableString(reader, 4), NullableString(reader, 5), NullableString(reader, 6), reader.GetInt64(7));
    }

    private static AdmissionRound ReadRound(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = LocalDatabase.Command(connection, "SELECT Id,ProgramId,CycleYear,EntryYear,Kind,RoundKey,Title,OfficialUrl,ApplicationUrl,Revision FROM AdmissionRound WHERE Id=$id", ("$id", id));
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("ROUND_NOT_FOUND");
        return new(reader.GetString(0), reader.GetString(1), NullableInt(reader, 2), NullableInt(reader, 3), Enum.Parse<AdmissionRoundKind>(reader.GetString(4)), reader.GetString(5), reader.GetString(6), NullableString(reader, 7), NullableString(reader, 8), reader.GetInt64(9));
    }

    private static string ScopeSchool(IReadOnlyDictionary<string, object?> scope) =>
        scope.TryGetValue("school", out var value) && value is string school && !string.IsNullOrWhiteSpace(school)
            ? school : throw new InvalidOperationException("INVALID_COLLECTOR_SCOPE");

    private static int? NormalizeYear(int? value) => value is >= 1900 and <= 2200 ? value : null;

    private static string[] SplitKinds(string value) => value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.Ordinal).ToArray() switch
    {
        { Length: > 0 } kinds => kinds,
        _ => throw new InvalidOperationException("INVALID_COLLECTOR_KIND")
    };

    private static DatePrecision DeterminePrecision(string? start, string? end)
    {
        var values = new[] { start, end }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (values.Length == 0) return DatePrecision.Unknown;
        if (values.All(value => DateOnly.TryParseExact(value, "yyyy-MM-dd", out _))) return DatePrecision.Date;
        if (values.All(value => DateTimeOffset.TryParse(value, out _)) && values.Any(value => value!.Contains('T') || value.Contains('Z') || value.Contains('+') || value.LastIndexOf('-') > 9))
            return DatePrecision.Instant;
        return DatePrecision.Approximate;
    }

    private static DateOnly? DateValue(string? value) => value is not null && DateOnly.TryParseExact(value, "yyyy-MM-dd", out var date) ? date : null;
    private static string? NullableString(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static int? NullableInt(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt32(index);
    private static string Now => DateTimeOffset.UtcNow.ToString("O");

    private static string StableId(string prefix, string value)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];
        return prefix + "-" + hash;
    }

    private sealed record RawRef([property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("json_pointer")] string? JsonPointer = null);

    private sealed record SourceRecord(
        [property: JsonPropertyName("external_id")] string ExternalId,
        [property: JsonPropertyName("school")] string School,
        [property: JsonPropertyName("department")] string? Department,
        [property: JsonPropertyName("source_year")] int? SourceYear,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("registration_start_raw")] string? RegistrationStartRaw,
        [property: JsonPropertyName("registration_end_raw")] string? RegistrationEndRaw,
        [property: JsonPropertyName("event_start_raw")] string? EventStartRaw,
        [property: JsonPropertyName("event_end_raw")] string? EventEndRaw,
        [property: JsonPropertyName("official_url")] string? OfficialUrl,
        [property: JsonPropertyName("application_url")] string? ApplicationUrl,
        [property: JsonPropertyName("source_url")] string SourceUrl,
        [property: JsonPropertyName("raw_ref")] RawRef RawRef);
}
