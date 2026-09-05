using ToolsTouch.Application;

namespace ToolsTouch.Infrastructure.Recommendation;

public sealed class EligibilityEvaluator : IEligibilityEvaluator
{
    public EligibilityResult Evaluate(IReadOnlyList<EligibilityCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        var conflicts = new List<string>();
        var missing = new List<string>();
        foreach (var check in checks)
        {
            if (string.IsNullOrWhiteSpace(check.Key)) throw new InvalidOperationException("ELIGIBILITY_KEY_REQUIRED");
            var reason = string.IsNullOrWhiteSpace(check.Reason) ? check.Key : check.Reason.Trim();
            if (!check.IsHardConstraint) continue;
            if (!check.IsReliableFormalSource || check.IsSatisfied is null)
            {
                missing.Add(reason);
                continue;
            }
            if (check.IsSatisfied == false) conflicts.Add(reason);
        }
        if (conflicts.Count > 0) return new(EligibilityState.Ineligible, conflicts, missing);
        if (missing.Count > 0 || checks.Count == 0) return new(EligibilityState.NeedsVerification, [], missing.Count == 0 ? ["NO_FORMAL_RULES"] : missing);
        return new(EligibilityState.Eligible, [], []);
    }
}
