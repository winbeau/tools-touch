using System.Text.Json;
using System.Text.Json.Serialization;
using ToolsTouch.Application;
using ToolsTouch.Core;
using ToolsTouch.Infrastructure.Recommendation;

static class RankingTests
{
    public static Task RunAsync(LocalDatabase database, string directory)
    {
        var fixture = JsonSerializer.Deserialize<RankingFixture>(File.ReadAllText(Path.Combine("docs", "design", "fixtures", "ranking-cases.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var ranker = new Ranker();
        foreach (var testCase in fixture.ScoreCases)
        {
            var components = testCase.Components.Select(component => new RankingComponent(component.Key, component.Weight, component.Score,
                component.Score is null ? [] : [$"fixture:{testCase.CaseId}:{component.Key}"])).ToArray();
            var actual = ranker.Evaluate(components, new WeightProfile(fixture.AlgorithmVersion));
            CheckClose(actual.KnownScore, testCase.Expected.KnownScore, testCase.CaseId + " known score");
            CheckClose(actual.Coverage, testCase.Expected.Coverage, testCase.CaseId + " coverage");
            CheckClose(actual.Lower, testCase.Expected.Lower, testCase.CaseId + " lower");
            CheckClose(actual.Upper, testCase.Expected.Upper, testCase.CaseId + " upper");
            Check(actual.EvidenceBucket.ToString() == testCase.Expected.EvidenceBucket, testCase.CaseId + " evidence bucket");
        }
        var invalidWeights = fixture.RejectionCases.Single(item => item.CaseId == "R06-invalid-weights");
        Check(Throws<InvalidOperationException>(() => ranker.Evaluate(invalidWeights.Components.Select(item =>
            new RankingComponent(item.Key, item.Weight, item.Score, ["fixture"])).ToArray(), new WeightProfile(fixture.AlgorithmVersion))),
            "invalid weight profiles are rejected");
        var invalidScore = fixture.RejectionCases.Single(item => item.CaseId == "R07-invalid-score");
        Check(Throws<InvalidOperationException>(() => ranker.Evaluate(invalidScore.Components.Select(item =>
            new RankingComponent(item.Key, item.Weight, item.Score, ["fixture"])).ToArray(), new WeightProfile(fixture.AlgorithmVersion))),
            "component scores outside 0..1 are rejected");

        var eligibility = new EligibilityEvaluator();
        Check(eligibility.Evaluate([new EligibilityCheck("gpa", false, true, true, "official conflict")]).State == EligibilityState.Ineligible &&
            eligibility.Evaluate([new EligibilityCheck("english", null, true, true, "missing score")]).State == EligibilityState.NeedsVerification &&
            eligibility.Evaluate([new EligibilityCheck("bar", false, true, false, "third party only")]).State == EligibilityState.NeedsVerification &&
            eligibility.Evaluate([new EligibilityCheck("degree", true, true, true)]).State == EligibilityState.Eligible,
            "eligibility evaluator distinguishes formal conflicts, missing facts, unreliable sources and passes");

        var candidates = new[]
        {
            Candidate("B", EligibilityState.Eligible, .5m, .9m, 2),
            Candidate("D", EligibilityState.Ineligible, 1m, .99m, 3),
            Candidate("C", EligibilityState.NeedsVerification, 1m, .95m, 3),
            Candidate("A2", EligibilityState.Eligible, 1m, .78m, 3),
            Candidate("A1", EligibilityState.Eligible, 1m, .78m, 3)
        };
        var sorted = ranker.Rank(candidates, new WeightProfile(fixture.AlgorithmVersion));
        Check(sorted.Items.Select(item => item.Candidate.TargetId).SequenceEqual(["A1", "A2", "B", "C"]) &&
            sorted.Excluded.Select(item => item.Candidate.TargetId).SequenceEqual(["D"]) && sorted.Items[0].Result.Rank == 1 &&
            sorted.Items[2].Result.EvidenceBucket == EvidenceBucket.Insufficient,
            "ranking groups eligibility and coverage before stable score and target ordering");

        var repository = new RecommendationRepository(database, new ArtifactStore(database.ArtifactDirectory));
        var input = new RecommendationRunInput("p05b-run", "DepartmentProgram", "p05-profile-input", "preference-one", 2026,
            fixture.AlgorithmVersion, "{\"minimumCoverage\":0.6}", "{\"targets\":[\"A1\",\"A2\",\"B\",\"C\",\"D\"],\"datasetRevision\":1}",
            new OrganizationRepository(database).Get().DataRevision, DateTimeOffset.UtcNow);
        var published = repository.Publish(input, sorted);
        var repeated = repository.Publish(input, sorted);
        Check(published.Id == repeated.Id && repository.GetCandidateSnapshot(input.RunId) == input.CandidateSnapshotJson &&
            repository.ListItems(input.RunId).Count == 5 && repository.ListItems(input.RunId).First().TargetId == "A1" &&
            repository.ListItems(input.RunId).Single(item => item.TargetId == "D").Rank is null &&
            repository.ListRuns().Single(item => item.Id == input.RunId).DatasetRevision == input.DatasetRevision &&
            Scalar(database, "SELECT COUNT(*) FROM Artifact WHERE Kind='recommendation-candidates'") == 1,
            "recommendation publish stores an immutable candidate snapshot and idempotently reopens the same run");
        Check(Throws<InvalidOperationException>(() => repository.Publish(input with { AlgorithmVersion = "rank-v2" }, sorted)),
            "same recommendation run ID rejects changed algorithm input");
        Console.WriteLine("PASS: fixture scoring, three-state eligibility, stable ranking and recommendation snapshot persistence");
        return Task.CompletedTask;
    }

    private static RankingCandidate Candidate(string id, EligibilityState state, decimal knownWeight, decimal score, int sourceQuality)
    {
        var eligibility = new EligibilityResult(state, state == EligibilityState.Ineligible ? ["formal conflict"] : [],
            state == EligibilityState.NeedsVerification ? ["missing"] : []);
        var components = knownWeight == 1m
            ? [new RankingComponent("match", 1m, score, [$"fixture:{id}"])]
            : new[] { new RankingComponent("match", knownWeight, score, [$"fixture:{id}"], null), new RankingComponent("unknown", 1m - knownWeight, null) };
        return new("DepartmentProgram", id, eligibility, components, sourceQuality);
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static void CheckClose(decimal? actual, decimal? expected, string message)
    {
        if (actual.HasValue != expected.HasValue || actual.HasValue && Math.Abs(actual.Value - expected!.Value) > .000000001m)
            throw new Exception($"{message}: actual={actual}, expected={expected}");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static int Scalar(LocalDatabase database, string sql)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, sql);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private sealed class RankingFixture
    {
        [JsonPropertyName("algorithm_version")]
        public string AlgorithmVersion { get; set; } = "";
        [JsonPropertyName("score_cases")]
        public List<ScoreCase> ScoreCases { get; set; } = [];
        [JsonPropertyName("rejection_cases")]
        public List<RejectionCase> RejectionCases { get; set; } = [];
    }

    private sealed class ScoreCase
    {
        [JsonPropertyName("case_id")]
        public string CaseId { get; set; } = "";
        public List<FixtureComponent> Components { get; set; } = [];
        public Expected Expected { get; set; } = new();
    }

    private sealed class RejectionCase
    {
        [JsonPropertyName("case_id")]
        public string CaseId { get; set; } = "";
        public List<FixtureComponent> Components { get; set; } = [];
    }

    private sealed class FixtureComponent
    {
        public string Key { get; set; } = "";
        public decimal Weight { get; set; }
        public decimal? Score { get; set; }
    }

    private sealed class Expected
    {
        [JsonPropertyName("known_score")]
        public decimal? KnownScore { get; set; }
        public decimal Coverage { get; set; }
        public decimal Lower { get; set; }
        public decimal Upper { get; set; }
        [JsonPropertyName("evidence_bucket")]
        public string EvidenceBucket { get; set; } = "";
    }
}
