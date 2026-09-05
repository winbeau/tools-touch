using Microsoft.Data.Sqlite;
using ToolsTouch.Application;
using ToolsTouch.Core;
using ToolsTouch.Infrastructure.Recommendation;

static class ProfilesStatisticsTests
{
    public static Task RunAsync(LocalDatabase database, string directory)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var profileId = "p05-profile-input";
        using (var connection = database.Open())
        using (var command = LocalDatabase.Command(connection, """
            INSERT INTO UserProfile(Id,Version,CvPath,CvHash,ExperiencesJson,Confirmed,CreatedAt)
            VALUES($id,(SELECT COALESCE(MAX(Version),0)+1 FROM UserProfile),'/managed/cv.pdf','cv-hash','{"extractedText":"candidate facts"}',0,$now)
            """, ("$id", profileId), ("$now", now))) command.ExecuteNonQuery();

        var profileService = new ProfileFactsService(database);
        var facts = new BackgroundFacts("985", "Computer Science", "major", 3, 100, 3.7m, 4m,
            "TOEFL", 105m, PaperFactState.KnownCount, 2, true);
        var revision = profileService.Confirm(new ProfileFactsInput(profileId, facts, "cv-extract-v2"));
        Check(revision.Confirmed && revision.Version > 0 && revision.StructuredFactsJson.Contains("985", StringComparison.Ordinal) &&
            Scalar(database, "SELECT COUNT(*) FROM UserProfile WHERE Id=$id", ("$id", profileId)) == 1,
            "confirmed profile facts create an immutable version with structured ranking, GPA and paper fields");
        Check(Throws<InvalidOperationException>(() => profileService.Confirm(new ProfileFactsInput(profileId,
            facts with { RankingNumerator = 101 }, "cv-extract-v2"))), "invalid rank facts are rejected before writing");

        var preferences = new PreferenceService(database);
        var parsed = preferences.Parse("想做大模型，不接受直博，希望允许实习，偏好上海");
        Check(parsed.Any(item => item.Key == "research_direction" && item.Strength == PreferenceConstraintStrength.Soft) &&
            parsed.Any(item => item.Key == "degree" && item.Strength == PreferenceConstraintStrength.Hard) &&
            parsed.Any(item => item.Key == "internship" && item.Strength == PreferenceConstraintStrength.Soft) &&
            parsed.Any(item => item.Key == "region" && item.Value == "上海"),
            "preference parser preserves explicit soft, hard and regional constraints");
        var preference = preferences.Save("想做大模型，不接受直博，希望允许实习，偏好上海", parsed, true);
        Check(preference.UserConfirmed && preference.Constraints.Count == parsed.Count && preference.Version >= 2,
            "preference revision stores the original prompt and confirmed parsed constraints");

        var organizations = new OrganizationRepository(database);
        var school = organizations.AddSchool(new School("p05-school", "Statistics University", "Statistics U"));
        var department = organizations.AddDepartment(new Department("p05-department", school.Id, "Computer Science", "Department"));
        var claimId = ScalarString(database, "SELECT Id FROM EvidenceClaim ORDER BY Id LIMIT 1")!;
        var statistics = new StatisticsBuilder(database);
        var key = new CohortStatisticsKey(department.Id, null, 2026, "SummerCamp", "Admitted");
        var samples = new[]
        {
            new AdmissionCaseSampleInput("p05-sample-01", department.Id, null, 2026, "SummerCamp", "Admitted",
                new BackgroundFacts("985", "CS", "major", 2, 100, 3.8m, 4m, PaperState: PaperFactState.KnownCount, PaperCount: 2), claimId, "post-1", "user-consented"),
            new AdmissionCaseSampleInput("p05-sample-01-copy", department.Id, null, 2026, "SummerCamp", "Admitted",
                new BackgroundFacts("985", "CS", "major", 2, 100, 3.8m, 4m, PaperState: PaperFactState.KnownCount, PaperCount: 2), claimId, "post-1", "user-consented"),
            new AdmissionCaseSampleInput("p05-sample-02", department.Id, null, 2026, "SummerCamp", "Admitted",
                new BackgroundFacts("211", "CS", "major", 5, 100, 3.2m, 4m, PaperState: PaperFactState.KnownZero, PaperCount: 0), claimId, "post-2", "user-consented"),
            new AdmissionCaseSampleInput("p05-sample-03", department.Id, null, 2026, "SummerCamp", "Admitted",
                new BackgroundFacts(PaperState: PaperFactState.Unknown), claimId, "post-3", "user-consented")
        };
        foreach (var sample in samples) statistics.ImportSample(sample);
        Check(statistics.ImportSample(samples[0]).Id == samples[0].Id &&
            Throws<InvalidOperationException>(() => statistics.ImportSample(samples[0] with { OutcomeStage = "Invited" })),
            "sample import is idempotent for identical records and rejects identity conflicts");
        var built = statistics.Build(key);
        var category = built.Single(item => item.Metric == "985Distribution");
        var ranking = built.Single(item => item.Metric == "RankingPercentiles");
        var papers = built.Single(item => item.Metric == "PaperCountDistribution");
        Check(built.Count == 4 && category.Denominator == 2 && category.UnknownCount == 1 && category.Numerator == 1 &&
            ranking.Denominator == 2 && ranking.UnknownCount == 1 && papers.Denominator == 2 && papers.UnknownCount == 1 &&
            category.DistributionJson.Contains("\"sample_size_sufficient\":false", StringComparison.Ordinal),
            "cohort statistics deduplicate samples and keep unknown ranking, category and paper values out of denominators");
        Check(Scalar(database, "SELECT COUNT(*) FROM CohortStatistic WHERE DepartmentId=$department", ("$department", department.Id)) == 4,
            "built cohort metrics are persisted as four versioned statistic records");
        Console.WriteLine("PASS: confirmed profile facts, preference strengths and cohort statistics with denominators, unknowns, zero and deduplication");
        return Task.CompletedTask;
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static int Scalar(LocalDatabase database, string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, sql, parameters);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string? ScalarString(LocalDatabase database, string sql)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, sql);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
