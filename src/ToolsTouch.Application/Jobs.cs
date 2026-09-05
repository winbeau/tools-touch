using ToolsTouch.Core;

namespace ToolsTouch.Application;

public sealed record JobBudget(int MaxToolCalls, int MaxModelRequests, int MaxRuntimeMilliseconds);

public sealed record JobStageDefinition(string StageKey, int Ordinal, string InputHash, string StableKey);

public sealed record JobDefinition(
    string Kind,
    string InputJson,
    string BudgetJson,
    string? Provider,
    string? Model,
    JobBudget Budget,
    IReadOnlyList<JobStageDefinition> Stages,
    string? ParentRunId = null,
    int MaxAttempts = 3);

public sealed record JobRunRecord(
    string Id,
    string Kind,
    string InputJson,
    string State,
    string Stage,
    string? CheckpointJson,
    string BudgetJson,
    string? Error,
    string? ParentRunId,
    string? Provider,
    string? Model,
    int MaxAttempts,
    string? LeaseOwner,
    DateTimeOffset? LeaseExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record JobStageRecord(
    string Id,
    string RunId,
    string StageKey,
    int Ordinal,
    string State,
    int AttemptCount,
    string? CheckpointJson,
    string? OutputJson,
    string? Error,
    DateTimeOffset? NextAttemptAt,
    string? LeaseOwner,
    DateTimeOffset? LeaseExpiresAt,
    string StableKey,
    string InputHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record JobAttemptRecord(
    string Id,
    string RunId,
    string StageId,
    string StageKey,
    int AttemptNumber,
    string OwnerSession,
    string State,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int LastSequence,
    string? Error,
    string InputHash);

public sealed record JobLease(string RunId, string StageId, string StageKey, string AttemptId, int AttemptNumber,
    string OwnerSession, DateTimeOffset LeaseExpiresAt, string InputHash);

public sealed record JobStageCommit(string? AnalysisJson = null, string? ProfessorId = null, string? ProfileId = null, string? Model = null);

public interface IJobRepository
{
    JobRunRecord Create(JobDefinition definition);
    JobRunRecord GetRun(string runId);
    IReadOnlyList<JobStageRecord> ListStages(string runId);
    JobLease? TryAcquireNext(string runId, string ownerSession, DateTimeOffset now, TimeSpan leaseDuration);
    bool RenewLease(JobLease lease, DateTimeOffset now, TimeSpan leaseDuration);
    void CompleteStage(JobLease lease, string outputJson, string checkpointJson, JobStageCommit? commit, DateTimeOffset now);
    void FailStage(JobLease lease, string error, bool retryable, DateTimeOffset now, TimeSpan retryDelay);
    void CancelStage(JobLease lease, string reason, DateTimeOffset now);
    void RequeueIncomplete(string runId, DateTimeOffset now);
    void CompleteRun(string runId, string ownerSession, DateTimeOffset now);
    int RecoverExpiredLeases(DateTimeOffset now);
    int RecoverStaleLeases(DateTimeOffset now);
    bool TryConsumeBudget(string runId, int toolCalls, int modelRequests);
}

public sealed class JobScheduler(IJobRepository repository)
{
    public JobRunRecord Create(JobDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.InputJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.BudgetJson);
        if (definition.MaxAttempts is < 1 or > 10) throw new ArgumentException("INVALID_MAX_ATTEMPTS");
        if (definition.Budget.MaxToolCalls < 1 || definition.Budget.MaxModelRequests < 1 || definition.Budget.MaxRuntimeMilliseconds < 1000)
            throw new ArgumentException("INVALID_JOB_BUDGET");
        if (definition.Stages.Count == 0 || definition.Stages.Select(stage => stage.StageKey).Distinct(StringComparer.Ordinal).Count() != definition.Stages.Count)
            throw new ArgumentException("INVALID_JOB_STAGES");
        if (!definition.Stages.Select(stage => stage.Ordinal).Order().SequenceEqual(Enumerable.Range(0, definition.Stages.Count)))
            throw new ArgumentException("INVALID_JOB_STAGE_ORDER");
        return repository.Create(definition);
    }

    public JobRunRecord GetRun(string runId) => repository.GetRun(runId);

    public JobLease? AcquireNext(string runId, string ownerSession, TimeSpan? leaseDuration = null, DateTimeOffset? now = null) =>
        repository.TryAcquireNext(runId, ownerSession, now ?? DateTimeOffset.UtcNow, leaseDuration ?? TimeSpan.FromMinutes(2));

    public bool Renew(JobLease lease, TimeSpan? leaseDuration = null, DateTimeOffset? now = null) =>
        repository.RenewLease(lease, now ?? DateTimeOffset.UtcNow, leaseDuration ?? TimeSpan.FromMinutes(2));

    public void Complete(JobLease lease, string outputJson, string checkpointJson, JobStageCommit? commit = null, DateTimeOffset? now = null) =>
        repository.CompleteStage(lease, outputJson, checkpointJson, commit, now ?? DateTimeOffset.UtcNow);

    public void Complete(JobLease lease, string outputJson, string checkpointJson, DateTimeOffset now) =>
        Complete(lease, outputJson, checkpointJson, null, now);

    public void Fail(JobLease lease, string error, bool retryable, TimeSpan? retryDelay = null, DateTimeOffset? now = null) =>
        repository.FailStage(lease, error, retryable, now ?? DateTimeOffset.UtcNow, retryDelay ?? TimeSpan.FromSeconds(1));

    public void Cancel(JobLease lease, string reason = "CANCELLED", DateTimeOffset? now = null) =>
        repository.CancelStage(lease, reason, now ?? DateTimeOffset.UtcNow);

    public void RequeueIncomplete(string runId, DateTimeOffset? now = null) =>
        repository.RequeueIncomplete(runId, now ?? DateTimeOffset.UtcNow);

    public void CompleteRun(string runId, string ownerSession, DateTimeOffset? now = null) =>
        repository.CompleteRun(runId, ownerSession, now ?? DateTimeOffset.UtcNow);

    public int Recover(DateTimeOffset? now = null) => repository.RecoverExpiredLeases(now ?? DateTimeOffset.UtcNow);

    public int RecoverStale(DateTimeOffset? now = null) => repository.RecoverStaleLeases(now ?? DateTimeOffset.UtcNow);

    public bool ConsumeBudget(string runId, int toolCalls = 0, int modelRequests = 0) =>
        repository.TryConsumeBudget(runId, toolCalls, modelRequests);
}
