using ToolsTouch.Application;

static class SemanticEvaluationTests
{
    public static Task RunAsync()
    {
        var service = new SemanticEvaluationService();
        var dimensions = new[]
        {
            new SemanticDimension("direction", "研究方向匹配", 0.7m),
            new SemanticDimension("preference", "培养方式与偏好", 0.3m),
        };
        var candidates = new[]
        {
            Candidate("ProfessorAppointment", "p-required", "required", mustInclude: true, baseScore: 0.2m),
            Candidate("ProfessorAppointment", "p-diverse", "diverse", mustInclude: true, diversity: "robotics", baseScore: 0.4m),
            Candidate("ProfessorAppointment", "p-high", "high", diversity: "language", baseScore: 0.9m),
            Candidate("ProfessorAppointment", "p-low", "low", diversity: "robotics", baseScore: 0.1m),
        };
        var plan = service.Plan("scope-1", candidates, new SemanticBudget(MaxCandidates: 3, MaxDeepResearchCandidates: 2));
        Check(plan.SelectedCandidates.Count == 3 && plan.SelectedCandidates.Any(item => item.TargetId == "p-required") &&
            plan.SelectedCandidates.Any(item => item.TargetId == "p-diverse") && plan.DeepResearchCandidates.Count == 2,
            "semantic budget selection preserves required and diversity candidates with deterministic deep research cap");

        var request = new SemanticEvaluationRequest("scope-1", "profile-1", "preference-1", 2026, "semantic-prompt-v1", dimensions,
            plan.SelectedCandidates, plan.Budget, plan.DeepResearchCandidates.Select(item => new SemanticTarget(item.TargetKind, item.TargetId)).ToArray());
        var prompt = service.BuildPrompt(request);
        Check(prompt.Contains("scope-1", StringComparison.Ordinal) && prompt.Contains("evidence_id", StringComparison.Ordinal) &&
            prompt.Contains("max_deep_research_candidates", StringComparison.Ordinal) && prompt.Contains("p-required", StringComparison.Ordinal) &&
            !prompt.Contains("send_email", StringComparison.Ordinal),
            "semantic prompt carries scope, evidence and budgets while retaining the no-send boundary");

        var valid = """
        {
          "candidate_scope_id":"scope-1",
          "evaluations":[
            {"target_kind":"ProfessorAppointment","target_id":"p-required","components":[
              {"key":"direction","score":0.75,"citations":[{"evidence_id":"p-required-e1","url":"https://example.org/p-required","claim":"Direction evidence"}]},
              {"key":"preference","score":null,"citations":[{"evidence_id":"p-required-e1","url":"https://example.org/p-required","claim":"Preference is not stated"}],"unknown_reason":"No reliable preference evidence"}
            ],"reasons":["The direction is supported by the cited source."]}
          ]
        }
        """;
        var result = service.Validate(request, valid);
        Check(result.EvaluatedCount == 1 && result.UnassessedTargets.Count == 2 && result.CandidateCoverage == 1m / 3m &&
            result.Evaluations[0].Components.Single(item => item.Key == "preference").Score is null,
            "valid semantic output keeps unknown dimensions and reports unassessed candidates");
        var ranking = service.ToRankingComponents(result.Evaluations[0], request);
        Check(ranking.Single(item => item.Key == "direction").EvaluationLevel == "Semantic" &&
            ranking.Single(item => item.Key == "preference").EvidenceIds!.SequenceEqual(["p-required-e1"]),
            "validated semantic dimensions map to traceable ranking components");

        Throws(() => service.Validate(request, valid.Replace("scope-1", "other-scope", StringComparison.Ordinal)), "scope mismatch is rejected");
        Throws(() => service.Validate(request, valid.Replace("p-required-e1", "foreign-evidence", StringComparison.Ordinal)), "citation outside candidate evidence is rejected");
        Throws(() => service.Validate(request, valid.Replace("\"score\":0.75", "\"score\":0.1", StringComparison.Ordinal)), "scores outside the rubric are rejected");
        Throws(() => service.Validate(request, valid.Replace(",\"unknown_reason\":\"No reliable preference evidence\"", "", StringComparison.Ordinal)), "unknown scores require an explanation");
        Throws(() => service.Validate(request, valid.Replace("\"evaluations\":[", "\"extra\":true,\"evaluations\":[", StringComparison.Ordinal)), "unknown output fields are rejected");
        Throws(() => service.Plan("scope-1", candidates, new SemanticBudget(MaxCandidates: 1)), "required candidates exceeding budget are rejected");
        Console.WriteLine("PASS: semantic prompt scope, rubric and citation validation, unknown coverage, fair budget selection");
        return Task.CompletedTask;
    }

    private static SemanticCandidate Candidate(string kind, string id, string evidenceClaim, bool mustInclude = false, string? diversity = null, decimal baseScore = 0)
        => new(kind, id, [new(id + "-e1", "https://example.org/" + id, evidenceClaim)], SourceQuality: 2,
            BaseScore: baseScore, MustInclude: mustInclude, DiversityBucket: diversity);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (InvalidOperationException error) when (error.Message.StartsWith("SEMANTIC_OUTPUT_INVALID:", StringComparison.Ordinal)) { return; }
        throw new Exception(message);
    }
}
