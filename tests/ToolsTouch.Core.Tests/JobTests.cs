using Microsoft.Data.Sqlite;
using ToolsTouch.Application;
using ToolsTouch.Core;

static class JobTests
{
    public static Task RunAsync(LocalDatabase database)
    {
        var repository = new JobRepository(database);
        var scheduler = new JobScheduler(repository);
        var budget = new JobBudget(3, 4, 120_000);
        var definition = new JobDefinition("Synthetic", "{\"request\":\"test\"}", "{\"max_tool_calls\":3}", "provider", "model", budget,
            [new JobStageDefinition("first", 0, "hash-first", "stable:first"), new JobStageDefinition("second", 1, "hash-second", "stable:second")]);
        var run = scheduler.Create(definition);
        Check(repository.ListStages(run.Id).All(stage => stage.State == "Queued"), "new jobs atomically create queued stages");
        var owner = "session-a";
        var now = DateTimeOffset.UtcNow;
        var first = scheduler.AcquireNext(run.Id, owner, TimeSpan.FromMinutes(2), now)!;
        Check(first.StageKey == "first" && first.AttemptNumber == 1 && first.InputHash == "hash-first", "scheduler leases the first stage with a stable attempt");
        Check(scheduler.ConsumeBudget(run.Id, toolCalls: 2), "root job budget accepts usage");
        Check(!scheduler.ConsumeBudget(run.Id, toolCalls: 2), "root job budget rejects usage beyond its total");
        Check(!scheduler.Renew(new JobLease(first.RunId, first.StageId, first.StageKey, first.AttemptId, first.AttemptNumber, "other-owner", first.LeaseExpiresAt, first.InputHash), now: now),
            "a different owner cannot renew a lease");
        Throws(() => scheduler.Complete(first, "{}", "{\"first\":{}}", now.AddMinutes(3)), "expired leases cannot commit stage output");
        Check(repository.ListStages(run.Id).Single(stage => stage.StageKey == "first").State == "Running", "stale completion leaves the running stage unchanged");
        Check(scheduler.Renew(first, TimeSpan.FromMinutes(2), now: now), "current owner can renew its lease");
        scheduler.Complete(first, "{\"value\":1}", "{\"first\":{\"value\":1}}", now.AddSeconds(1));
        Check(repository.ListStages(run.Id).Single(stage => stage.StageKey == "first").State == "Completed", "stage output and checkpoint commit together");

        var second = scheduler.AcquireNext(run.Id, owner, TimeSpan.FromMinutes(2), now.AddSeconds(2))!;
        Check(second.StageKey == "second" && second.AttemptNumber == 1, "next stage waits for its predecessor");
        scheduler.Fail(second, "temporary", retryable: true, TimeSpan.Zero, now.AddSeconds(3));
        Check(repository.ListStages(run.Id).Single(stage => stage.StageKey == "second").State == "RetryScheduled", "retryable failure schedules a later attempt");
        var retry = scheduler.AcquireNext(run.Id, owner, TimeSpan.FromMinutes(2), now.AddSeconds(4))!;
        Check(retry.AttemptId != second.AttemptId && retry.AttemptNumber == 2, "retry receives a new attempt id");
        scheduler.Complete(retry, "{\"value\":2}", "{\"first\":{\"value\":1},\"second\":{\"value\":2}}", now.AddSeconds(5));
        scheduler.CompleteRun(run.Id, owner, now.AddSeconds(6));
        Check(repository.GetRun(run.Id).State == "Completed", "completed stages atomically close the job");

        var interrupted = scheduler.Create(new JobDefinition("Synthetic", "{}", "{}", "provider", "model", new(5, 5, 120_000),
            [new JobStageDefinition("only", 0, "hash", "stable:only")]));
        var abandoned = scheduler.AcquireNext(interrupted.Id, owner, TimeSpan.FromSeconds(1), now)!;
        Check(scheduler.Recover(now.AddSeconds(2)) == 1, "expired leases are recovered");
        Check(repository.ListStages(interrupted.Id).Single().State == "Queued", "expired stage returns to the queue");
        var recovered = scheduler.AcquireNext(interrupted.Id, owner, TimeSpan.FromMinutes(2), now.AddSeconds(3))!;
        Check(recovered.AttemptNumber == 2 && recovered.AttemptId != abandoned.AttemptId, "recovery never reuses an interrupted attempt");

        var parent = scheduler.Create(new JobDefinition("Parent", "{}", "{}", null, null, new(2, 2, 10_000),
            [new JobStageDefinition("parent", 0, "p", "stable:p")]));
        var child = scheduler.Create(new JobDefinition("Child", "{}", "{}", null, null, new(2, 2, 10_000),
            [new JobStageDefinition("child", 0, "c", "stable:c")], parent.Id));
        Check(repository.GetRun(child.Id).ParentRunId == parent.Id, "parent-child job relation is persisted");
        Check(scheduler.ConsumeBudget(parent.Id, toolCalls: 2), "parent consumes shared budget");
        Check(!scheduler.ConsumeBudget(child.Id, toolCalls: 1), "child cannot bypass the parent total budget");
        Console.WriteLine("PASS: job queue atomic acceptance, stage lease/retry/recovery, attempt identity, parent relation and shared budget");
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws(Action action, string message)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception(message);
    }
}
