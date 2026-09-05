using System.Text.Json.Serialization;

namespace ToolsTouch.Application;

public sealed record BackgroundFacts(
    string? UndergraduateCategory = null,
    string? Major = null,
    string? RankingType = null,
    int? RankingNumerator = null,
    int? RankingDenominator = null,
    decimal? Gpa = null,
    decimal? GpaScale = null,
    string? EnglishTest = null,
    decimal? EnglishScore = null,
    PaperFactState PaperState = PaperFactState.Unknown,
    int? PaperCount = null,
    bool? HasResearchExperience = null);

public enum PaperFactState
{
    Unknown,
    KnownZero,
    KnownCount
}

public sealed record ProfileFactsInput(string ProfileId, BackgroundFacts Facts, string ExtractionVersion);

public sealed record ProfileFactsRevision(
    string Id,
    int Version,
    string CvPath,
    string CvHash,
    string ExperiencesJson,
    string StructuredFactsJson,
    string ExtractionVersion,
    bool Confirmed,
    DateTimeOffset ConfirmedAt);

public interface IProfileFactsService
{
    ProfileFactsRevision Confirm(ProfileFactsInput input);
}

public enum PreferenceConstraintStrength
{
    Hard,
    Soft,
    Ambiguous
}

public sealed record PreferenceConstraint(
    string Key,
    string Value,
    PreferenceConstraintStrength Strength,
    string SourceText);

public sealed record PreferenceRevisionRecord(
    string Id,
    int Version,
    string? PromptText,
    IReadOnlyList<PreferenceConstraint> Constraints,
    bool UserConfirmed,
    DateTimeOffset CreatedAt);

public interface IPreferenceService
{
    IReadOnlyList<PreferenceConstraint> Parse(string? promptText);
    PreferenceRevisionRecord Save(string? promptText, IReadOnlyList<PreferenceConstraint> constraints, bool userConfirmed);
}

public sealed record AdmissionCaseSampleInput(
    string Id,
    string DepartmentId,
    string? ProgramId,
    int CycleYear,
    string RoundKind,
    string OutcomeStage,
    BackgroundFacts Facts,
    string ClaimId,
    string DedupGroup,
    string ConsentOrigin);

public sealed record AdmissionCaseSampleRecord(
    string Id,
    string DepartmentId,
    string? ProgramId,
    int CycleYear,
    string RoundKind,
    string OutcomeStage,
    BackgroundFacts Facts,
    string ClaimId,
    string DedupGroup,
    string ConsentOrigin,
    DateTimeOffset CreatedAt);

public sealed record CohortStatisticsKey(
    string DepartmentId,
    string? ProgramId,
    int CycleYear,
    string RoundKind,
    string OutcomeStage);

public sealed record CohortStatisticRecord(
    string Id,
    CohortStatisticsKey Key,
    string Metric,
    string DefinitionVersion,
    int? Numerator,
    int Denominator,
    int UnknownCount,
    string DistributionJson,
    IReadOnlyList<string> SampleIds,
    DateTimeOffset BuiltAt);

public interface IStatisticsBuilder
{
    AdmissionCaseSampleRecord ImportSample(AdmissionCaseSampleInput input);
    IReadOnlyList<CohortStatisticRecord> Build(CohortStatisticsKey key);
}
