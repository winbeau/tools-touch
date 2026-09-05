using ToolsTouch.Core;

namespace ToolsTouch.Application;

public sealed record RecommendationRunInput(
    string RunId,
    string Level,
    string ProfileId,
    string? PreferenceId,
    int TargetCycleYear,
    string AlgorithmVersion,
    string WeightProfileJson,
    string CandidateSnapshotJson,
    long DatasetRevision,
    DateTimeOffset AsOf,
    string State = "Completed",
    string BudgetJson = "{}");

public sealed record RecommendationRunRecord(
    string Id,
    string Level,
    string ProfileId,
    string? PreferenceId,
    int TargetCycleYear,
    string AlgorithmVersion,
    string WeightProfileJson,
    string CandidateSnapshotArtifactId,
    long DatasetRevision,
    DateTimeOffset AsOf,
    string State,
    string BudgetJson,
    DateTimeOffset CreatedAt);

public sealed record RecommendationItemRecord(
    string RunId,
    string TargetKind,
    string TargetId,
    EligibilityState Eligibility,
    int? Rank,
    decimal? Score,
    decimal ScoreLower,
    decimal ScoreUpper,
    EvidenceBucket ConfidenceLabel,
    string ComponentsJson,
    string ReasonsJson,
    string MissingFactsJson,
    string EvidenceIdsJson,
    int SourceQuality,
    int? DisplayOrder);

public interface IRecommendationRepository
{
    RecommendationRunRecord Publish(RecommendationRunInput input, RankingRunResult result);
    IReadOnlyList<RecommendationRunRecord> ListRuns(string? profileId = null);
    RecommendationRunRecord GetRun(string runId);
    string GetCandidateSnapshot(string runId);
    IReadOnlyList<RecommendationItemRecord> ListItems(string runId);
}
