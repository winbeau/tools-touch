using ToolsTouch.Application;

namespace ToolsTouch.Infrastructure.Recommendation;

public sealed class Ranker : IRanker
{
    private const decimal Tolerance = 0.000000001m;

    public RankingResult Evaluate(IReadOnlyList<RankingComponent> components, WeightProfile profile)
    {
        ArgumentNullException.ThrowIfNull(components);
        ValidateProfile(profile);
        if (components.Count == 0) return new(null, 0, 0, 100, EvidenceBucket.Insufficient);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        decimal totalWeight = 0;
        decimal knownWeight = 0;
        decimal knownValue = 0;
        foreach (var component in components)
        {
            if (string.IsNullOrWhiteSpace(component.Key) || !seen.Add(component.Key)) throw new InvalidOperationException("DUPLICATE_COMPONENT_KEY");
            if (component.Weight < 0) throw new InvalidOperationException("INVALID_WEIGHT_PROFILE");
            totalWeight += component.Weight;
            if (component.Score is { } score)
            {
                if (score < 0 || score > 1) throw new InvalidOperationException("INVALID_COMPONENT_SCORE");
                if ((component.EvidenceIds is null or { Count: 0 }) && component.EvaluationLevel is not ("Rule" or "UserFact"))
                    throw new InvalidOperationException("COMPONENT_EVIDENCE_REQUIRED");
                knownWeight += component.Weight;
                knownValue += component.Weight * score;
            }
        }
        if (Math.Abs(totalWeight - 1m) > Tolerance) throw new InvalidOperationException("INVALID_WEIGHT_PROFILE");
        decimal? knownScore = knownWeight == 0 ? null : knownValue / knownWeight * 100m;
        var lower = knownValue * 100m;
        var upper = lower + (totalWeight - knownWeight) * 100m;
        return new(knownScore, knownWeight, lower, upper, knownWeight >= profile.MinimumCoverage ? EvidenceBucket.Sufficient : EvidenceBucket.Insufficient);
    }

    public RankingRunResult Rank(IReadOnlyList<RankingCandidate> candidates, WeightProfile profile)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ValidateProfile(profile);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var evaluated = candidates.Select(candidate =>
        {
            if (string.IsNullOrWhiteSpace(candidate.TargetKind) || string.IsNullOrWhiteSpace(candidate.TargetId) || !ids.Add(candidate.TargetKind + "\0" + candidate.TargetId))
                throw new InvalidOperationException("DUPLICATE_RANK_TARGET");
            if (candidate.SourceQuality < 0) throw new InvalidOperationException("INVALID_SOURCE_QUALITY");
            return new RankedCandidate(candidate, Evaluate(candidate.Components, profile));
        }).ToArray();
        var excluded = evaluated.Where(item => item.Candidate.Eligibility.State == EligibilityState.Ineligible).ToArray();
        var visible = evaluated.Where(item => item.Candidate.Eligibility.State != EligibilityState.Ineligible)
            .GroupBy(item => item.Candidate.Eligibility.State)
            .OrderBy(group => group.Key == EligibilityState.Eligible ? 0 : 1)
            .SelectMany(group => group.OrderBy(item => item.Result.EvidenceBucket == EvidenceBucket.Insufficient ? 1 : 0)
                .ThenByDescending(item => item.Result.Lower)
                .ThenByDescending(item => item.Result.KnownScore ?? decimal.MinValue)
                .ThenByDescending(item => item.Candidate.SourceQuality)
                .ThenBy(item => item.Candidate.TargetId, StringComparer.Ordinal)
                .Select((item, index) => item with { Result = item.Result with { Rank = index + 1 } }))
            .ToArray();
        visible = visible.Select((item, index) => item with { Result = item.Result with { DisplayOrder = index + 1 } }).ToArray();
        var excludedWithResult = excluded.Select(item => item with { Result = item.Result with { Rank = null, DisplayOrder = null } }).ToArray();
        return new(visible, excludedWithResult);
    }

    private static void ValidateProfile(WeightProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.AlgorithmVersion) || profile.MinimumCoverage is < 0 or > 1)
            throw new InvalidOperationException("INVALID_WEIGHT_PROFILE");
    }
}
