using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using ToolsTouch.Application;
using ToolsTouch.Core;

namespace ToolsTouch.Infrastructure.Recommendation;

public sealed class StatisticsBuilder(LocalDatabase database) : IStatisticsBuilder
{
    private const string DefinitionVersion = "cohort-statistics-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public AdmissionCaseSampleRecord ImportSample(AdmissionCaseSampleInput input)
    {
        Validate(input);
        var factsJson = JsonSerializer.Serialize(input.Facts, JsonOptions);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using (var existing = LocalDatabase.Command(connection, """
            SELECT Id,DepartmentId,ProgramId,CycleYear,RoundKind,OutcomeStage,BackgroundJson,ClaimId,DedupGroup,ConsentOrigin,CreatedAt
            FROM AdmissionCaseSample WHERE Id=$id
            """, ("$id", input.Id)))
        {
            existing.Transaction = transaction;
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                var same = reader.GetString(1) == input.DepartmentId && Nullable(reader, 2) == input.ProgramId && reader.GetInt32(3) == input.CycleYear &&
                    reader.GetString(4) == input.RoundKind && reader.GetString(5) == input.OutcomeStage && reader.GetString(6) == factsJson &&
                    reader.GetString(7) == input.ClaimId && reader.GetString(8) == input.DedupGroup && reader.GetString(9) == input.ConsentOrigin;
                if (!same) throw new InvalidOperationException("SAMPLE_IDEMPOTENCY_CONFLICT");
                return ReadSample(reader);
            }
        }
        var now = DateTimeOffset.UtcNow;
        using var insert = LocalDatabase.Command(connection, """
            INSERT INTO AdmissionCaseSample(Id,DepartmentId,ProgramId,CycleYear,RoundKind,OutcomeStage,BackgroundJson,ClaimId,DedupGroup,ConsentOrigin,CreatedAt)
            VALUES($id,$department,$program,$cycle,$round,$outcome,$background,$claim,$dedup,$consent,$now)
            """, ("$id", input.Id), ("$department", input.DepartmentId), ("$program", input.ProgramId), ("$cycle", input.CycleYear),
            ("$round", input.RoundKind), ("$outcome", input.OutcomeStage), ("$background", factsJson), ("$claim", input.ClaimId),
            ("$dedup", input.DedupGroup), ("$consent", input.ConsentOrigin), ("$now", now.ToString("O")));
        insert.Transaction = transaction;
        insert.ExecuteNonQuery();
        transaction.Commit();
        return new(input.Id, input.DepartmentId, input.ProgramId, input.CycleYear, input.RoundKind, input.OutcomeStage,
            input.Facts, input.ClaimId, input.DedupGroup, input.ConsentOrigin, now);
    }

    public IReadOnlyList<CohortStatisticRecord> Build(CohortStatisticsKey key)
    {
        Validate(key);
        var samples = LoadSamples(key).GroupBy(sample => sample.DedupGroup, StringComparer.Ordinal)
            .Select(group => group.OrderBy(sample => sample.Id, StringComparer.Ordinal).First()).OrderBy(sample => sample.Id, StringComparer.Ordinal).ToArray();
        if (samples.Length == 0) return [];
        var sampleIds = samples.Select(sample => sample.Id).ToArray();
        var builtAt = DateTimeOffset.UtcNow;
        var statistics = new[]
        {
            Build985(key, samples, sampleIds, builtAt),
            BuildRanking(key, samples, sampleIds, builtAt),
            BuildGpa(key, samples, sampleIds, builtAt),
            BuildPapers(key, samples, sampleIds, builtAt)
        };
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        foreach (var statistic in statistics)
        {
            using var command = LocalDatabase.Command(connection, """
                INSERT OR IGNORE INTO CohortStatistic(Id,DepartmentId,ProgramId,CycleYear,RoundKind,OutcomeStage,Metric,DefinitionVersion,Numerator,Denominator,UnknownCount,DistributionJson,SampleIdsJson,BuiltAt)
                VALUES($id,$department,$program,$cycle,$round,$outcome,$metric,$definition,$numerator,$denominator,$unknown,$distribution,$samples,$built)
                """, ("$id", statistic.Id), ("$department", key.DepartmentId), ("$program", key.ProgramId), ("$cycle", key.CycleYear),
                ("$round", key.RoundKind), ("$outcome", key.OutcomeStage), ("$metric", statistic.Metric), ("$definition", statistic.DefinitionVersion),
                ("$numerator", statistic.Numerator), ("$denominator", statistic.Denominator), ("$unknown", statistic.UnknownCount),
                ("$distribution", statistic.DistributionJson), ("$samples", JsonSerializer.Serialize(sampleIds)), ("$built", statistic.BuiltAt.ToString("O")));
            command.Transaction = transaction;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return statistics;
    }

    private IReadOnlyList<AdmissionCaseSampleRecord> LoadSamples(CohortStatisticsKey key)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            SELECT Id,DepartmentId,ProgramId,CycleYear,RoundKind,OutcomeStage,BackgroundJson,ClaimId,DedupGroup,ConsentOrigin,CreatedAt
            FROM AdmissionCaseSample
            WHERE DepartmentId=$department AND ProgramId IS $program AND CycleYear=$cycle AND RoundKind=$round AND OutcomeStage=$outcome
            ORDER BY Id
            """, ("$department", key.DepartmentId), ("$program", key.ProgramId), ("$cycle", key.CycleYear),
            ("$round", key.RoundKind), ("$outcome", key.OutcomeStage));
        using var reader = command.ExecuteReader();
        var samples = new List<AdmissionCaseSampleRecord>();
        while (reader.Read()) samples.Add(ReadSample(reader));
        return samples;
    }

    private static CohortStatisticRecord Build985(CohortStatisticsKey key, IReadOnlyList<AdmissionCaseSampleRecord> samples,
        IReadOnlyList<string> ids, DateTimeOffset builtAt)
    {
        var known = samples.Where(sample => !string.IsNullOrWhiteSpace(sample.Facts.UndergraduateCategory)).ToArray();
        var distribution = known.GroupBy(sample => sample.Facts.UndergraduateCategory!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var json = JsonSerializer.Serialize(new { metric = "undergraduate_category", counts = distribution, sample_size_sufficient = known.Length >= 10 }, JsonOptions);
        return Make(key, "985Distribution", Count(distribution, "985"), known.Length, samples.Count - known.Length, json, ids, builtAt);
    }

    private static CohortStatisticRecord BuildRanking(CohortStatisticsKey key, IReadOnlyList<AdmissionCaseSampleRecord> samples,
        IReadOnlyList<string> ids, DateTimeOffset builtAt)
    {
        var known = samples.Where(sample => sample.Facts.RankingNumerator is not null && sample.Facts.RankingDenominator is not null &&
            !string.IsNullOrWhiteSpace(sample.Facts.RankingType)).ToArray();
        var types = known.GroupBy(sample => sample.Facts.RankingType!, StringComparer.Ordinal).ToDictionary(group => group.Key,
            group => new { count = group.Count(), rank_percent = Percentiles(group.Select(sample => 100m * sample.Facts.RankingNumerator!.Value / sample.Facts.RankingDenominator!.Value).ToArray()),
                ranking_direction = "smaller_is_better", sample_size_sufficient = group.Count() >= 10 }, StringComparer.Ordinal);
        var json = JsonSerializer.Serialize(new { metric = "ranking_percentile", types }, JsonOptions);
        return Make(key, "RankingPercentiles", null, known.Length, samples.Count - known.Length, json, ids, builtAt);
    }

    private static CohortStatisticRecord BuildGpa(CohortStatisticsKey key, IReadOnlyList<AdmissionCaseSampleRecord> samples,
        IReadOnlyList<string> ids, DateTimeOffset builtAt)
    {
        var known = samples.Where(sample => sample.Facts.Gpa is not null && sample.Facts.GpaScale is not null).ToArray();
        var scales = known.GroupBy(sample => sample.Facts.GpaScale!.Value).ToDictionary(group => group.Key.ToString("0.####"),
            group => new { count = group.Count(), values = Percentiles(group.Select(sample => sample.Facts.Gpa!.Value).ToArray()), sample_size_sufficient = group.Count() >= 10 }, StringComparer.Ordinal);
        var json = JsonSerializer.Serialize(new { metric = "gpa", scales }, JsonOptions);
        return Make(key, "GpaDistribution", null, known.Length, samples.Count - known.Length, json, ids, builtAt);
    }

    private static CohortStatisticRecord BuildPapers(CohortStatisticsKey key, IReadOnlyList<AdmissionCaseSampleRecord> samples,
        IReadOnlyList<string> ids, DateTimeOffset builtAt)
    {
        var known = samples.Where(sample => sample.Facts.PaperState is PaperFactState.KnownZero or PaperFactState.KnownCount).ToArray();
        var values = known.Select(sample => (decimal)(sample.Facts.PaperCount ?? 0)).ToArray();
        var json = JsonSerializer.Serialize(new { metric = "paper_count", known_zero = known.Count(sample => sample.Facts.PaperState == PaperFactState.KnownZero),
            known_positive = known.Count(sample => sample.Facts.PaperState == PaperFactState.KnownCount), values = Percentiles(values), sample_size_sufficient = known.Length >= 10 }, JsonOptions);
        return Make(key, "PaperCountDistribution", null, known.Length, samples.Count - known.Length, json, ids, builtAt);
    }

    private static CohortStatisticRecord Make(CohortStatisticsKey key, string metric, int? numerator, int denominator, int unknown,
        string distribution, IReadOnlyList<string> sampleIds, DateTimeOffset builtAt)
    {
        var identity = string.Join("|", key.DepartmentId, key.ProgramId, key.CycleYear, key.RoundKind, key.OutcomeStage, metric, DefinitionVersion, string.Join(",", sampleIds));
        var id = "stat-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..32];
        return new(id, key, metric, DefinitionVersion, numerator, denominator, unknown, distribution, sampleIds, builtAt);
    }

    private static object Percentiles(decimal[] values)
    {
        if (values.Length == 0) return new { count = 0, min = (decimal?)null, p25 = (decimal?)null, median = (decimal?)null, p75 = (decimal?)null, max = (decimal?)null };
        Array.Sort(values);
        return new { count = values.Length, min = values[0], p25 = Percentile(values, .25), median = Percentile(values, .5), p75 = Percentile(values, .75), max = values[^1] };
    }

    private static decimal Percentile(decimal[] values, double percentile)
    {
        if (values.Length == 1) return values[0];
        var index = (values.Length - 1) * percentile;
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        return values[lower] + (values[upper] - values[lower]) * (decimal)(index - lower);
    }

    private static int Count(IReadOnlyDictionary<string, int> values, string key) => values.TryGetValue(key, out var count) ? count : 0;

    private static AdmissionCaseSampleRecord ReadSample(SqliteDataReader reader, string? forcedId = null)
    {
        var facts = JsonSerializer.Deserialize<BackgroundFacts>(reader.GetString(6), JsonOptions) ?? new();
        return new(forcedId ?? reader.GetString(0), reader.GetString(1), Nullable(reader, 2), reader.GetInt32(3), reader.GetString(4), reader.GetString(5), facts,
            reader.GetString(7), reader.GetString(8), reader.GetString(9), DateTimeOffset.Parse(reader.GetString(10)));
    }

    private static void Validate(AdmissionCaseSampleInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Id) || string.IsNullOrWhiteSpace(input.DepartmentId) || string.IsNullOrWhiteSpace(input.RoundKind) ||
            string.IsNullOrWhiteSpace(input.OutcomeStage) || string.IsNullOrWhiteSpace(input.ClaimId) || string.IsNullOrWhiteSpace(input.DedupGroup) ||
            string.IsNullOrWhiteSpace(input.ConsentOrigin) || input.CycleYear is < 1900 or > 2200)
            throw new InvalidOperationException("SAMPLE_INPUT_INVALID");
        if (input.Facts.RankingNumerator is not null || input.Facts.RankingDenominator is not null || !string.IsNullOrWhiteSpace(input.Facts.RankingType))
        {
            if (string.IsNullOrWhiteSpace(input.Facts.RankingType) || input.Facts.RankingNumerator is null || input.Facts.RankingDenominator is null ||
                input.Facts.RankingNumerator < 1 || input.Facts.RankingDenominator < input.Facts.RankingNumerator)
                throw new InvalidOperationException("SAMPLE_RANKING_FACT_INVALID");
        }
        if (input.Facts.Gpa is not null || input.Facts.GpaScale is not null)
        {
            if (input.Facts.Gpa is null || input.Facts.GpaScale is null || input.Facts.Gpa < 0 || input.Facts.GpaScale <= 0 || input.Facts.Gpa > input.Facts.GpaScale)
                throw new InvalidOperationException("SAMPLE_GPA_FACT_INVALID");
        }
        if (input.Facts.PaperState == PaperFactState.KnownZero && input.Facts.PaperCount != 0 ||
            input.Facts.PaperState == PaperFactState.KnownCount && input.Facts.PaperCount is null or < 1 ||
            input.Facts.PaperState == PaperFactState.Unknown && input.Facts.PaperCount is not null)
            throw new InvalidOperationException("SAMPLE_PAPER_FACT_INVALID");
    }

    private static void Validate(CohortStatisticsKey key)
    {
        if (string.IsNullOrWhiteSpace(key.DepartmentId) || string.IsNullOrWhiteSpace(key.RoundKind) || string.IsNullOrWhiteSpace(key.OutcomeStage) ||
            key.CycleYear is < 1900 or > 2200) throw new InvalidOperationException("STATISTICS_KEY_INVALID");
    }

    private static string? Nullable(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
}
