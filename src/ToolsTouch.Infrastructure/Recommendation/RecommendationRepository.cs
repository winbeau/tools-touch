using System.Text;
using System.Text.Json;
using ToolsTouch.Application;
using ToolsTouch.Core;

namespace ToolsTouch.Infrastructure.Recommendation;

public sealed class RecommendationRepository(LocalDatabase database, ArtifactStore artifacts) : IRecommendationRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public RecommendationRunRecord Publish(RecommendationRunInput input, RankingRunResult result)
    {
        Validate(input, result);
        var snapshot = artifacts.Stage("recommendation-candidates", Encoding.UTF8.GetBytes(input.CandidateSnapshotJson), "application/json", ".json");
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using (var existing = LocalDatabase.Command(connection, "SELECT AlgorithmVersion,WeightProfileJson,CandidateSnapshotArtifactId,DatasetRevision FROM RecommendationRun WHERE Id=$id", ("$id", input.RunId)))
        {
            existing.Transaction = transaction;
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                var same = reader.GetString(0) == input.AlgorithmVersion && reader.GetString(1) == input.WeightProfileJson &&
                    reader.GetInt64(3) == input.DatasetRevision && reader.GetString(2) == snapshot.Id;
                if (!same) throw new InvalidOperationException("RECOMMENDATION_RUN_CONFLICT");
                reader.Close();
                return GetRun(connection, input.RunId);
            }
        }
        ArtifactStore.Insert(connection, transaction, snapshot);
        using (var run = LocalDatabase.Command(connection, """
            INSERT INTO RecommendationRun(Id,Level,ProfileId,PreferenceId,TargetCycleYear,AlgorithmVersion,WeightProfileJson,CandidateSnapshotArtifactId,DatasetRevision,AsOf,State,BudgetJson,CreatedAt)
            VALUES($id,$level,$profile,$preference,$cycle,$algorithm,$weights,$snapshot,$revision,$asof,$state,$budget,$created)
            """, ("$id", input.RunId), ("$level", input.Level), ("$profile", input.ProfileId), ("$preference", input.PreferenceId),
            ("$cycle", input.TargetCycleYear), ("$algorithm", input.AlgorithmVersion), ("$weights", input.WeightProfileJson), ("$snapshot", snapshot.Id),
            ("$revision", input.DatasetRevision), ("$asof", input.AsOf.ToString("O")), ("$state", input.State), ("$budget", input.BudgetJson), ("$created", DateTimeOffset.UtcNow.ToString("O"))))
        {
            run.Transaction = transaction;
            run.ExecuteNonQuery();
        }
        foreach (var item in result.Items.Concat(result.Excluded))
        {
            var components = JsonSerializer.Serialize(item.Candidate.Components, JsonOptions);
            var evidenceIds = item.Candidate.Components.SelectMany(component => component.EvidenceIds ?? []).Distinct(StringComparer.Ordinal).ToArray();
            using var insert = LocalDatabase.Command(connection, """
                INSERT INTO RecommendationItem(RunId,TargetKind,TargetId,Eligibility,Rank,Score,ScoreLower,ScoreUpper,ConfidenceLabel,ComponentsJson,ReasonsJson,MissingFactsJson,EvidenceIdsJson,SourceQuality,DisplayOrder)
                VALUES($run,$kind,$target,$eligibility,$rank,$score,$lower,$upper,$confidence,$components,$reasons,$missing,$evidence,$quality,$display)
                """, ("$run", input.RunId), ("$kind", item.Candidate.TargetKind), ("$target", item.Candidate.TargetId),
                ("$eligibility", item.Candidate.Eligibility.State.ToString()), ("$rank", item.Result.Rank), ("$score", item.Result.KnownScore),
                ("$lower", item.Result.Lower), ("$upper", item.Result.Upper), ("$confidence", item.Result.EvidenceBucket.ToString()),
                ("$components", components), ("$reasons", JsonSerializer.Serialize(item.Candidate.Reasons ?? [], JsonOptions)),
                ("$missing", JsonSerializer.Serialize((item.Candidate.MissingFacts ?? []).Concat(item.Candidate.Eligibility.MissingFacts).Distinct(StringComparer.Ordinal), JsonOptions)),
                ("$evidence", JsonSerializer.Serialize(evidenceIds, JsonOptions)), ("$quality", item.Candidate.SourceQuality), ("$display", item.Result.DisplayOrder));
            insert.Transaction = transaction;
            insert.ExecuteNonQuery();
        }
        using (var revision = connection.CreateCommand())
        {
            revision.Transaction = transaction;
            revision.CommandText = "UPDATE WorkspaceMeta SET DataRevision=DataRevision+1 WHERE Id=1";
            revision.ExecuteNonQuery();
        }
        transaction.Commit();
        return GetRun(connection, input.RunId);
    }

    public RecommendationRunRecord GetRun(string runId)
    {
        using var connection = database.Open();
        return GetRun(connection, runId);
    }

    public IReadOnlyList<RecommendationRunRecord> ListRuns(string? profileId = null)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            SELECT Id,Level,ProfileId,PreferenceId,TargetCycleYear,AlgorithmVersion,WeightProfileJson,CandidateSnapshotArtifactId,DatasetRevision,AsOf,State,BudgetJson,CreatedAt
            FROM RecommendationRun
            WHERE $profile IS NULL OR ProfileId=$profile
            ORDER BY CreatedAt DESC,Id DESC
            """, ("$profile", profileId));
        using var reader = command.ExecuteReader();
        var runs = new List<RecommendationRunRecord>();
        while (reader.Read()) runs.Add(ReadRun(reader));
        return runs;
    }

    public string GetCandidateSnapshot(string runId)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            SELECT a.Id,a.Kind,a.RelativePath,a.ContentHash,a.ByteLength,a.MimeType,a.CreatedAt
            FROM RecommendationRun r JOIN Artifact a ON a.Id=r.CandidateSnapshotArtifactId WHERE r.Id=$id
            """, ("$id", runId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("RECOMMENDATION_RUN_NOT_FOUND");
        var artifact = new Artifact(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4),
            reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6)));
        return ArtifactStore.ReadText(artifact, artifacts.RootDirectory);
    }

    public IReadOnlyList<RecommendationItemRecord> ListItems(string runId)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            SELECT TargetKind,TargetId,Eligibility,Rank,Score,ScoreLower,ScoreUpper,ConfidenceLabel,ComponentsJson,ReasonsJson,MissingFactsJson,EvidenceIdsJson,SourceQuality,DisplayOrder
            FROM RecommendationItem WHERE RunId=$run ORDER BY COALESCE(DisplayOrder,2147483647),TargetKind,TargetId
            """, ("$run", runId));
        using var reader = command.ExecuteReader();
        var items = new List<RecommendationItemRecord>();
        while (reader.Read()) items.Add(new(runId, reader.GetString(0), reader.GetString(1), Enum.Parse<EligibilityState>(reader.GetString(2)),
            NullableInt(reader, 3), NullableDecimal(reader, 4), Convert.ToDecimal(reader.GetValue(5)), Convert.ToDecimal(reader.GetValue(6)), Enum.Parse<EvidenceBucket>(reader.GetString(7)),
            reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11), reader.GetInt32(12), NullableInt(reader, 13)));
        return items;
    }

    private static RecommendationRunRecord GetRun(Microsoft.Data.Sqlite.SqliteConnection connection, string runId)
    {
        using var command = LocalDatabase.Command(connection, """
            SELECT Id,Level,ProfileId,PreferenceId,TargetCycleYear,AlgorithmVersion,WeightProfileJson,CandidateSnapshotArtifactId,DatasetRevision,AsOf,State,BudgetJson,CreatedAt
            FROM RecommendationRun WHERE Id=$id
            """, ("$id", runId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("RECOMMENDATION_RUN_NOT_FOUND");
        return ReadRun(reader);
    }

    private static RecommendationRunRecord ReadRun(Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), Nullable(reader, 3), reader.GetInt32(4), reader.GetString(5),
            reader.GetString(6), reader.GetString(7), reader.GetInt64(8), DateTimeOffset.Parse(reader.GetString(9)), reader.GetString(10), reader.GetString(11), DateTimeOffset.Parse(reader.GetString(12)));

    private static void Validate(RecommendationRunInput input, RankingRunResult result)
    {
        if (string.IsNullOrWhiteSpace(input.RunId) || string.IsNullOrWhiteSpace(input.Level) || string.IsNullOrWhiteSpace(input.ProfileId) ||
            input.TargetCycleYear is < 1900 or > 2200 || string.IsNullOrWhiteSpace(input.AlgorithmVersion) || string.IsNullOrWhiteSpace(input.WeightProfileJson) ||
            string.IsNullOrWhiteSpace(input.CandidateSnapshotJson) || input.DatasetRevision < 0 || string.IsNullOrWhiteSpace(input.State) || string.IsNullOrWhiteSpace(input.BudgetJson))
            throw new InvalidOperationException("RECOMMENDATION_INPUT_INVALID");
        using var _ = JsonDocument.Parse(input.WeightProfileJson);
        using var __ = JsonDocument.Parse(input.CandidateSnapshotJson);
        foreach (var item in result.Items.Concat(result.Excluded))
            if (item.Candidate.TargetKind is not ("DepartmentProgram" or "ProfessorAppointment")) throw new InvalidOperationException("RECOMMENDATION_TARGET_INVALID");
    }

    private static string? Nullable(Microsoft.Data.Sqlite.SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static int? NullableInt(Microsoft.Data.Sqlite.SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt32(index);
    private static decimal? NullableDecimal(Microsoft.Data.Sqlite.SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetDecimal(index);
}
