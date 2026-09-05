namespace ToolsTouch.Application;

public enum EligibilityState
{
    Eligible,
    Ineligible,
    NeedsVerification
}

public sealed record EligibilityCheck(
    string Key,
    bool? IsSatisfied,
    bool IsHardConstraint,
    bool IsReliableFormalSource,
    string? Reason = null,
    IReadOnlyList<string>? EvidenceIds = null);

public sealed record EligibilityResult(
    EligibilityState State,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> MissingFacts);

public interface IEligibilityEvaluator
{
    EligibilityResult Evaluate(IReadOnlyList<EligibilityCheck> checks);
}

public sealed record RankingComponent(
    string Key,
    decimal Weight,
    decimal? Score,
    IReadOnlyList<string>? EvidenceIds = null,
    string? UnknownReason = null,
    string EvaluationLevel = "Rule");

public sealed record WeightProfile(
    string AlgorithmVersion,
    decimal MinimumCoverage = 0.6m);

public enum EvidenceBucket
{
    Sufficient,
    Insufficient
}

public sealed record RankingResult(
    decimal? KnownScore,
    decimal Coverage,
    decimal Lower,
    decimal Upper,
    EvidenceBucket EvidenceBucket,
    int? Rank = null,
    int? DisplayOrder = null);

public sealed record RankingCandidate(
    string TargetKind,
    string TargetId,
    EligibilityResult Eligibility,
    IReadOnlyList<RankingComponent> Components,
    int SourceQuality,
    IReadOnlyList<string>? Reasons = null,
    IReadOnlyList<string>? MissingFacts = null);

public sealed record RankedCandidate(
    RankingCandidate Candidate,
    RankingResult Result);

public sealed record RankingRunResult(
    IReadOnlyList<RankedCandidate> Items,
    IReadOnlyList<RankedCandidate> Excluded);

public interface IRanker
{
    RankingResult Evaluate(IReadOnlyList<RankingComponent> components, WeightProfile profile);
    RankingRunResult Rank(IReadOnlyList<RankingCandidate> candidates, WeightProfile profile);
}
