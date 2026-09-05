using Microsoft.Data.Sqlite;
using ToolsTouch.Application;

namespace ToolsTouch.Core;

public sealed class SourceRepository(LocalDatabase database, ArtifactStore artifacts) : ISourceRepository
{
    private static string Now => DateTimeOffset.UtcNow.ToString("O");

    public SourceSnapshot SaveSnapshot(SourceSnapshot snapshot, ReadOnlyMemory<byte> content, string artifactKind,
        string mimeType, string extension = ".bin")
    {
        var artifact = artifacts.Stage(artifactKind, content, mimeType, extension);
        if (!string.Equals(snapshot.ContentHash, artifact.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SNAPSHOT_HASH_MISMATCH");
        var published = new SourceSnapshot(snapshot.Id, snapshot.SourceKey, snapshot.OriginalUrl, snapshot.CanonicalUrl,
            snapshot.ExternalRecordId, snapshot.ContentHash, snapshot.FetchedAt, snapshot.PublishedAt, snapshot.SourceYear,
            snapshot.Transport, snapshot.ParseVersion, artifact.Id, snapshot.Title);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        ArtifactStore.Insert(connection, transaction, artifact);
        InsertSnapshot(connection, transaction, published);
        IncrementRevision(connection, transaction);
        transaction.Commit();
        return published;
    }

    public EvidenceClaim AddClaim(EvidenceClaim claim, IReadOnlyList<ClaimSupport>? supports = null)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO EvidenceClaim(Id,SnapshotId,SubjectKind,SubjectId,ClaimType,ValueJson,QuotedText,LocatorJson,OriginKind,ConfidenceLabel,ValidFrom,ValidUntil,SupersedesId)
            VALUES($id,$snapshot,$subjectKind,$subject,$claimType,$value,$quoted,$locator,$origin,$confidence,$from,$until,$supersedes)
            """, ("$id", claim.Id), ("$snapshot", claim.SnapshotId), ("$subjectKind", claim.SubjectKind), ("$subject", claim.SubjectId),
            ("$claimType", claim.ClaimType), ("$value", claim.ValueJson), ("$quoted", claim.QuotedText), ("$locator", claim.LocatorJson),
            ("$origin", claim.OriginKind), ("$confidence", claim.ConfidenceLabel), ("$from", claim.ValidFrom?.ToString("O")),
            ("$until", claim.ValidUntil?.ToString("O")), ("$supersedes", claim.SupersedesId));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
        foreach (var support in supports ?? [])
        {
            using var link = LocalDatabase.Command(connection, "INSERT INTO ClaimSupport(ClaimId,SnapshotId,LocatorJson) VALUES($claim,$snapshot,$locator)",
                ("$claim", support.ClaimId), ("$snapshot", support.SnapshotId), ("$locator", support.LocatorJson));
            link.Transaction = transaction;
            link.ExecuteNonQuery();
        }
        IncrementRevision(connection, transaction);
        transaction.Commit();
        return claim;
    }

    public SourceCheck AddSourceCheck(SourceCheck check)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO SourceCheck(Id,SnapshotId,CheckedAt,State,ETag,LastModified,Error)
            VALUES($id,$snapshot,$checked,$state,$etag,$modified,$error)
            """, ("$id", check.Id), ("$snapshot", check.SnapshotId), ("$checked", check.CheckedAt.ToString("O")),
            ("$state", check.State), ("$etag", check.ETag), ("$modified", check.LastModified), ("$error", check.Error));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
        IncrementRevision(connection, transaction);
        transaction.Commit();
        return check;
    }

    public EvidenceConflict AddConflict(EvidenceConflict conflict)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO EvidenceConflict(Id,SubjectKind,SubjectId,ClaimType,ClaimIdsJson,Resolution,ResolvedBy,ResolvedAt)
            VALUES($id,$kind,$subject,$claimType,$claims,$resolution,$by,$at)
            """, ("$id", conflict.Id), ("$kind", conflict.SubjectKind), ("$subject", conflict.SubjectId),
            ("$claimType", conflict.ClaimType), ("$claims", conflict.ClaimIdsJson), ("$resolution", conflict.Resolution),
            ("$by", conflict.ResolvedBy), ("$at", conflict.ResolvedAt?.ToString("O")));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
        IncrementRevision(connection, transaction);
        transaction.Commit();
        return conflict;
    }

    public WindowObservation AddObservation(WindowObservation observation)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = LocalDatabase.Command(connection, """
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
            ("$observed", observation.ObservedAt.ToString("O")), ("$supersedes", observation.SupersedesId));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
        IncrementRevision(connection, transaction);
        transaction.Commit();
        return observation;
    }

    public HistoricalProjection AddProjection(HistoricalProjection projection)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO HistoricalProjection(Id,ProgramId,TargetCycleYear,Kind,RoundKey,SourceObservationId,ProjectedStart,ProjectedEndExclusive,MethodVersion,GeneratedAt,ProjectionIssue)
            VALUES($id,$program,$target,$kind,$roundKey,$observation,$start,$end,$method,$generated,$issue)
            """, ("$id", projection.Id), ("$program", projection.ProgramId), ("$target", projection.TargetCycleYear),
            ("$kind", projection.Kind), ("$roundKey", projection.RoundKey), ("$observation", projection.SourceObservationId),
            ("$start", projection.ProjectedStart), ("$end", projection.ProjectedEndExclusive), ("$method", projection.MethodVersion),
            ("$generated", projection.GeneratedAt.ToString("O")), ("$issue", projection.ProjectionIssue));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
        IncrementRevision(connection, transaction);
        transaction.Commit();
        return projection;
    }

    public ObservationOverride AddOverride(ObservationOverride observationOverride)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO ObservationOverride(Id,SubjectId,FieldKey,ValueJson,BasedOnClaimId,Revision,Reason,CreatedAt,RevokedAt)
            VALUES($id,$subject,$field,$value,$claim,$revision,$reason,$created,$revoked)
            """, ("$id", observationOverride.Id), ("$subject", observationOverride.SubjectId), ("$field", observationOverride.FieldKey),
            ("$value", observationOverride.ValueJson), ("$claim", observationOverride.BasedOnClaimId), ("$revision", observationOverride.Revision),
            ("$reason", observationOverride.Reason), ("$created", observationOverride.CreatedAt.ToString("O")),
            ("$revoked", observationOverride.RevokedAt?.ToString("O")));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
        IncrementRevision(connection, transaction);
        transaction.Commit();
        return observationOverride;
    }

    public void RevokeOverride(string id, long revision)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = LocalDatabase.Command(connection, """
            UPDATE ObservationOverride SET RevokedAt=$now,Revision=Revision+1
            WHERE Id=$id AND Revision=$revision AND RevokedAt IS NULL
            """, ("$now", Now), ("$id", id), ("$revision", revision));
        command.Transaction = transaction;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("OVERRIDE_REVISION_CONFLICT");
        IncrementRevision(connection, transaction);
        transaction.Commit();
    }

    public PreferenceRevision AddPreferenceRevision(PreferenceRevision preference)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO PreferenceRevision(Id,Version,PromptText,ParsedConstraintsJson,UserConfirmed,CreatedAt)
            VALUES($id,$version,$prompt,$constraints,$confirmed,$created)
            """, ("$id", preference.Id), ("$version", preference.Version), ("$prompt", preference.PromptText),
            ("$constraints", preference.ParsedConstraintsJson), ("$confirmed", preference.UserConfirmed ? 1 : 0),
            ("$created", preference.CreatedAt.ToString("O")));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
        IncrementRevision(connection, transaction);
        transaction.Commit();
        return preference;
    }

    public Artifact GetArtifact(string id)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "SELECT Id,Kind,RelativePath,ContentHash,ByteLength,MimeType,CreatedAt FROM Artifact WHERE Id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("ARTIFACT_NOT_FOUND");
        return new Artifact(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6)));
    }

    public SourceSnapshot GetSnapshot(string id)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            SELECT Id,SourceKey,OriginalUrl,CanonicalUrl,ExternalRecordId,ContentHash,FetchedAt,PublishedAt,SourceYear,Transport,ParseVersion,ArtifactId,Title
            FROM SourceSnapshot WHERE Id=$id
            """, ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("SNAPSHOT_NOT_FOUND");
        return ReadSnapshot(reader);
    }

    public IReadOnlyList<SourceSnapshot> ListSnapshots(string sourceKey, string? externalRecordId = null)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            SELECT Id,SourceKey,OriginalUrl,CanonicalUrl,ExternalRecordId,ContentHash,FetchedAt,PublishedAt,SourceYear,Transport,ParseVersion,ArtifactId,Title
            FROM SourceSnapshot WHERE SourceKey=$source AND ($external IS NULL OR ExternalRecordId=$external)
            ORDER BY FetchedAt,Id
            """, ("$source", sourceKey), ("$external", externalRecordId));
        using var reader = command.ExecuteReader();
        var result = new List<SourceSnapshot>();
        while (reader.Read()) result.Add(ReadSnapshot(reader));
        return result;
    }

    public IReadOnlyList<EvidenceClaim> ListClaims(string subjectKind, string subjectId)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            SELECT Id,SnapshotId,SubjectKind,SubjectId,ClaimType,ValueJson,QuotedText,LocatorJson,OriginKind,ConfidenceLabel,ValidFrom,ValidUntil,SupersedesId
            FROM EvidenceClaim WHERE SubjectKind=$kind AND SubjectId=$id ORDER BY Id
            """, ("$kind", subjectKind), ("$id", subjectId));
        using var reader = command.ExecuteReader();
        var result = new List<EvidenceClaim>();
        while (reader.Read()) result.Add(ReadClaim(reader));
        return result;
    }

    public ObservationOverride GetActiveOverride(string subjectId, string fieldKey)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            SELECT Id,SubjectId,FieldKey,ValueJson,BasedOnClaimId,Revision,Reason,CreatedAt,RevokedAt
            FROM ObservationOverride WHERE SubjectId=$subject AND FieldKey=$field AND RevokedAt IS NULL
            ORDER BY Revision DESC LIMIT 1
            """, ("$subject", subjectId), ("$field", fieldKey));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("ACTIVE_OVERRIDE_NOT_FOUND");
        return new ObservationOverride(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            NullableString(reader, 4), reader.GetInt64(5), reader.GetString(6), DateTimeOffset.Parse(reader.GetString(7)),
            reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)));
    }

    private static void InsertSnapshot(SqliteConnection connection, SqliteTransaction transaction, SourceSnapshot snapshot)
    {
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO SourceSnapshot(Id,SourceKey,OriginalUrl,CanonicalUrl,ExternalRecordId,ContentHash,FetchedAt,PublishedAt,SourceYear,Transport,ArtifactId,ParseVersion,Title)
            VALUES($id,$source,$original,$canonical,$external,$hash,$fetched,$published,$year,$transport,$artifact,$parse,$title)
            """, ("$id", snapshot.Id), ("$source", snapshot.SourceKey), ("$original", snapshot.OriginalUrl), ("$canonical", snapshot.CanonicalUrl),
            ("$external", snapshot.ExternalRecordId), ("$hash", snapshot.ContentHash), ("$fetched", snapshot.FetchedAt.ToString("O")),
            ("$published", snapshot.PublishedAt?.ToString("O")), ("$year", snapshot.SourceYear), ("$transport", snapshot.Transport),
            ("$artifact", snapshot.ArtifactId), ("$parse", snapshot.ParseVersion), ("$title", snapshot.Title));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private static void IncrementRevision(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = LocalDatabase.Command(connection, "UPDATE WorkspaceMeta SET DataRevision=DataRevision+1 WHERE Id=1");
        command.Transaction = transaction;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("WORKSPACE_META_MISSING");
    }

    private static SourceSnapshot ReadSnapshot(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), NullableString(reader, 2),
        NullableString(reader, 3), NullableString(reader, 4), reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6)),
        reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7)), NullableInt(reader, 8), reader.GetString(9), reader.GetString(10),
        NullableString(reader, 11), NullableString(reader, 12));
    private static EvidenceClaim ReadClaim(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.GetString(5), NullableString(reader, 6), NullableString(reader, 7), reader.GetString(8), reader.GetString(9),
        reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10)), reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11)), NullableString(reader, 12));
    private static string? NullableString(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static int? NullableInt(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt32(index);
}
