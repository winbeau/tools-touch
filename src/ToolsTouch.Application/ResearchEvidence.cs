using ToolsTouch.Core;

namespace ToolsTouch.Application;

public sealed record HomepageEvidenceInput(
    string ProfessorId,
    string Url,
    string Title,
    string Text,
    DateTimeOffset FetchedAt);

public sealed record HomepageEvidenceSummary(
    string ProfessorId,
    string SnapshotId,
    int ClaimCount,
    string? PublicEmail,
    long DatasetRevision);

public enum PaperAttributionState
{
    Attributed,
    NeedsVerification
}

public sealed record PaperAttributionResult(
    string PaperId,
    PaperAttributionState State,
    string? ProfessorId,
    IReadOnlyList<string> MatchingAuthorNames,
    string? ResolutionId,
    string? EvidenceClaimId,
    long DatasetRevision);

public sealed record PaperReadInput(
    string PaperId,
    string? ContentHash,
    int? StartPage,
    int? EndPage,
    string ReadScope,
    string ExtractorVersion,
    ReadOnlyMemory<byte>? TextContent = null);

public sealed record PaperReadResult(
    string Id,
    string PaperId,
    string ReadScope,
    int? StartPage,
    int? EndPage,
    string? TextArtifactId,
    long DatasetRevision);

public sealed record ProfessorEvaluationInput(
    string ProfessorId,
    string SourceUrl,
    string SourceType,
    DateTimeOffset? PostedAt,
    string Summary,
    string TopicsJson,
    string RawText,
    DateTimeOffset FetchedAt,
    string VerificationState = "Unverified");

public sealed record ProfessorEvaluationResult(
    string ProfessorId,
    string EvaluationId,
    string SnapshotId,
    string EvidenceClaimId,
    long DatasetRevision);

public interface IFacultyResearchService
{
    HomepageEvidenceSummary ImportHomepageEvidence(HomepageEvidenceInput input);
    PaperAttributionResult ImportPaper(Paper paper, string professorId);
    PaperReadResult RecordPaperRead(PaperReadInput input);
    ProfessorEvaluationResult ImportEvaluation(ProfessorEvaluationInput input);
}
