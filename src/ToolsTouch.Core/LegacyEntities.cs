namespace ToolsTouch.Core;

// These records are shared domain data. Persistence and transport implementations stay in Infrastructure.
public sealed record Evidence(string Url, string Claim);
public sealed record Professor(string Id, string Name, string Institution, string? Homepage, string? Email, Evidence[] Evidence);
public sealed record Paper(string Id, string ExternalId, string Title, string[] Authors, int? Year, string? Abstract, string SourceUrl, string? LocalPath, string ParseStatus);
public sealed record UserProfile(string Id, int Version, string CvPath, string CvHash, string ExperiencesJson, bool Confirmed);
public sealed record AgentRun(string Id, string Kind, string InputJson, string State, string Stage, string? CheckpointJson, string BudgetJson, string? Error);
