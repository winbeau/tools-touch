using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ToolsTouch.Application;

namespace ToolsTouch.Core;

public sealed class JobRepository(LocalDatabase database) : IJobRepository
{
    private static string Now(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static string NewId() => Guid.NewGuid().ToString("N");

    public JobRunRecord Create(JobDefinition definition)
    {
        var runId = NewId();
        var now = DateTimeOffset.UtcNow;
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        if (definition.ParentRunId is not null)
        {
            using var parent = LocalDatabase.Command(connection, "SELECT Id FROM AgentRun WHERE Id=$id", ("$id", definition.ParentRunId));
            parent.Transaction = transaction;
            if (parent.ExecuteScalar() is null) throw new KeyNotFoundException("PARENT_RUN_NOT_FOUND");
        }
        using (var insert = LocalDatabase.Command(connection, """
            INSERT INTO AgentRun(Id,Kind,InputJson,State,Stage,BudgetJson,CreatedAt,UpdatedAt,ParentRunId,Provider,Model,MaxAttempts)
            VALUES($id,$kind,$input,'Queued','Prepare',$budget,$now,$now,$parent,$provider,$model,$attempts)
            """, ("$id", runId), ("$kind", definition.Kind), ("$input", definition.InputJson), ("$budget", definition.BudgetJson),
            ("$now", Now(now)), ("$parent", definition.ParentRunId), ("$provider", definition.Provider), ("$model", definition.Model), ("$attempts", definition.MaxAttempts)))
        {
            insert.Transaction = transaction;
            insert.ExecuteNonQuery();
        }
        var rootRunId = definition.ParentRunId is null ? runId : RootRunId(connection, transaction, definition.ParentRunId);
        using (var budget = LocalDatabase.Command(connection, """
            INSERT INTO JobBudget(RunId,RootRunId,MaxToolCalls,MaxModelRequests,MaxRuntimeMilliseconds)
            VALUES($run,$root,$tools,$models,$runtime)
            """, ("$run", runId), ("$root", rootRunId), ("$tools", definition.Budget.MaxToolCalls),
            ("$models", definition.Budget.MaxModelRequests), ("$runtime", definition.Budget.MaxRuntimeMilliseconds)))
        {
            budget.Transaction = transaction;
            budget.ExecuteNonQuery();
        }
        foreach (var stage in definition.Stages)
        {
            var stageId = NewId();
            using var insert = LocalDatabase.Command(connection, """
                INSERT INTO JobStage(Id,RunId,StageKey,Ordinal,State,StableKey,InputHash,CreatedAt,UpdatedAt)
                VALUES($id,$run,$key,$ordinal,'Queued',$stable,$hash,$now,$now)
                """, ("$id", stageId), ("$run", runId), ("$key", stage.StageKey), ("$ordinal", stage.Ordinal),
                ("$stable", stage.StableKey), ("$hash", stage.InputHash), ("$now", Now(now)));
            insert.Transaction = transaction;
            insert.ExecuteNonQuery();
        }
        IncrementRevision(connection, transaction);
        AppendEvent(connection, transaction, runId, "job_created", null, null, ResearchStore.Serialize(new { state = "Queued" }), now);
        transaction.Commit();
        return GetRun(runId);
    }

    public JobRunRecord GetRun(string runId)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            SELECT Id,Kind,InputJson,State,Stage,CheckpointJson,BudgetJson,Error,ParentRunId,Provider,Model,MaxAttempts,LeaseOwner,LeaseExpiresAt,CreatedAt,UpdatedAt
            FROM AgentRun WHERE Id=$id
            """, ("$id", runId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("RUN_NOT_FOUND");
        return ReadRun(reader);
    }

    public IReadOnlyList<JobStageRecord> ListStages(string runId)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            SELECT Id,RunId,StageKey,Ordinal,State,AttemptCount,CheckpointJson,OutputJson,Error,NextAttemptAt,LeaseOwner,LeaseExpiresAt,StableKey,InputHash,CreatedAt,UpdatedAt
            FROM JobStage WHERE RunId=$run ORDER BY Ordinal,Id
            """, ("$run", runId));
        using var reader = command.ExecuteReader();
        var result = new List<JobStageRecord>();
        while (reader.Read()) result.Add(ReadStage(reader));
        return result;
    }

    public JobLease? TryAcquireNext(string runId, string ownerSession, DateTimeOffset now, TimeSpan leaseDuration)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSession);
        var expires = now.Add(leaseDuration);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = LocalDatabase.Command(connection, """
            SELECT s.Id,s.RunId,s.StageKey,s.AttemptCount,s.InputHash
            FROM JobStage s JOIN AgentRun r ON r.Id=s.RunId
            WHERE s.RunId=$run AND r.State NOT IN ('Completed','Cancelled','Failed') AND s.State IN ('Queued','RetryScheduled')
              AND (s.NextAttemptAt IS NULL OR s.NextAttemptAt <= $now)
              AND s.AttemptCount < r.MaxAttempts
              AND NOT EXISTS (SELECT 1 FROM JobStage prior WHERE prior.RunId=s.RunId AND prior.Ordinal<s.Ordinal AND prior.State<>'Completed')
            ORDER BY s.Ordinal,s.Id LIMIT 1
            """, ("$run", runId), ("$now", Now(now)));
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) { transaction.Rollback(); return null; }
        var stageId = reader.GetString(0); var foundRunId = reader.GetString(1); var stageKey = reader.GetString(2);
        var attemptNumber = reader.GetInt32(3) + 1; var inputHash = reader.GetString(4); reader.Close();
        var attemptId = NewId();
        using (var update = LocalDatabase.Command(connection, """
            UPDATE JobStage SET State='Running',AttemptCount=AttemptCount+1,Error=NULL,NextAttemptAt=NULL,LeaseOwner=$owner,LeaseExpiresAt=$expires,UpdatedAt=$now
            WHERE Id=$stage AND State IN ('Queued','RetryScheduled')
            """, ("$owner", ownerSession), ("$expires", Now(expires)), ("$now", Now(now)), ("$stage", stageId)))
        {
            update.Transaction = transaction;
            if (update.ExecuteNonQuery() != 1) { transaction.Rollback(); return null; }
        }
        using (var insert = LocalDatabase.Command(connection, """
            INSERT INTO JobAttempt(Id,RunId,StageId,StageKey,AttemptNumber,OwnerSession,State,StartedAt,LastSequence,InputHash)
            VALUES($id,$run,$stage,$key,$number,$owner,'Running',$now,1,$hash)
            """, ("$id", attemptId), ("$run", foundRunId), ("$stage", stageId), ("$key", stageKey), ("$number", attemptNumber),
            ("$owner", ownerSession), ("$now", Now(now)), ("$hash", inputHash)))
        {
            insert.Transaction = transaction;
            insert.ExecuteNonQuery();
        }
        using (var updateRun = LocalDatabase.Command(connection, """
            UPDATE AgentRun SET State='Running',Stage=$stage,LeaseOwner=$owner,LeaseExpiresAt=$expires,Error=NULL,UpdatedAt=$now WHERE Id=$run
            """, ("$stage", stageKey), ("$owner", ownerSession), ("$expires", Now(expires)), ("$now", Now(now)), ("$run", foundRunId)))
        {
            updateRun.Transaction = transaction;
            updateRun.ExecuteNonQuery();
        }
        AppendEvent(connection, transaction, foundRunId, "attempt_started", attemptId, 1, ResearchStore.Serialize(new { stage_key = stageKey, attempt_number = attemptNumber }), now);
        transaction.Commit();
        return new JobLease(foundRunId, stageId, stageKey, attemptId, attemptNumber, ownerSession, expires, inputHash);
    }

    public bool RenewLease(JobLease lease, DateTimeOffset now, TimeSpan leaseDuration)
    {
        var expires = now.Add(leaseDuration);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var stage = LocalDatabase.Command(connection, "UPDATE JobStage SET LeaseExpiresAt=$expires,UpdatedAt=$now WHERE Id=$id AND State='Running' AND LeaseOwner=$owner", ("$expires", Now(expires)), ("$now", Now(now)), ("$id", lease.StageId), ("$owner", lease.OwnerSession));
        stage.Transaction = transaction;
        if (stage.ExecuteNonQuery() != 1) { transaction.Rollback(); return false; }
        using var run = LocalDatabase.Command(connection, "UPDATE AgentRun SET LeaseExpiresAt=$expires,UpdatedAt=$now WHERE Id=$run AND LeaseOwner=$owner", ("$expires", Now(expires)), ("$now", Now(now)), ("$run", lease.RunId), ("$owner", lease.OwnerSession));
        run.Transaction = transaction;
        if (run.ExecuteNonQuery() != 1) { transaction.Rollback(); return false; }
        transaction.Commit();
        return true;
    }

    public void CompleteStage(JobLease lease, string outputJson, string checkpointJson, JobStageCommit? commit, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        EnsureLease(connection, transaction, lease, now);
        using (var stage = LocalDatabase.Command(connection, """
            UPDATE JobStage SET State='Completed',OutputJson=$output,CheckpointJson=$checkpoint,Error=NULL,LeaseOwner=NULL,LeaseExpiresAt=NULL,UpdatedAt=$now
            WHERE Id=$id AND State='Running' AND LeaseOwner=$owner
            """, ("$output", outputJson), ("$checkpoint", checkpointJson), ("$now", Now(now)), ("$id", lease.StageId), ("$owner", lease.OwnerSession)))
        {
            stage.Transaction = transaction; if (stage.ExecuteNonQuery() != 1) throw new InvalidOperationException("STALE_JOB_LEASE");
        }
        FinishAttempt(connection, transaction, lease, "Completed", null, 2, now);
        using (var run = LocalDatabase.Command(connection, "UPDATE AgentRun SET CheckpointJson=$checkpoint,Stage=$stage,LeaseOwner=NULL,LeaseExpiresAt=NULL,Error=NULL,UpdatedAt=$now WHERE Id=$run AND LeaseOwner=$owner", ("$checkpoint", checkpointJson), ("$stage", lease.StageKey), ("$now", Now(now)), ("$run", lease.RunId), ("$owner", lease.OwnerSession)))
        {
            run.Transaction = transaction; run.ExecuteNonQuery();
        }
        if (commit?.AnalysisJson is not null)
        {
            if (string.IsNullOrWhiteSpace(commit.ProfessorId) || string.IsNullOrWhiteSpace(commit.Model)) throw new InvalidOperationException("INVALID_STAGE_COMMIT");
            using var analysis = LocalDatabase.Command(connection, """
                INSERT INTO ProfessorAnalysis(Id,ProfessorId,ProfileId,ContentJson,Model,CreatedAt)
                VALUES($id,$prof,$profile,$content,$model,$now)
                ON CONFLICT(Id) DO UPDATE SET ProfessorId=excluded.ProfessorId,ProfileId=excluded.ProfileId,ContentJson=excluded.ContentJson,Model=excluded.Model,CreatedAt=excluded.CreatedAt
                """, ("$id", lease.RunId), ("$prof", commit.ProfessorId), ("$profile", commit.ProfileId), ("$content", commit.AnalysisJson), ("$model", commit.Model), ("$now", Now(now)));
            analysis.Transaction = transaction;
            analysis.ExecuteNonQuery();
        }
        IncrementRevision(connection, transaction);
        AppendEvent(connection, transaction, lease.RunId, "stage_completed", lease.AttemptId, 2, ResearchStore.Serialize(new { stage_key = lease.StageKey }), now);
        transaction.Commit();
    }

    public void FailStage(JobLease lease, string error, bool retryable, DateTimeOffset now, TimeSpan retryDelay)
    {
        FinishStage(lease, error, retryable, cancelled: false, now, retryDelay);
    }

    public void CancelStage(JobLease lease, string reason, DateTimeOffset now)
    {
        FinishStage(lease, reason, retryable: false, cancelled: true, now, TimeSpan.Zero);
    }

    public void RequeueIncomplete(string runId, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var stages = LocalDatabase.Command(connection, """
            UPDATE JobStage SET State='Queued',Error=NULL,NextAttemptAt=NULL,LeaseOwner=NULL,LeaseExpiresAt=NULL,UpdatedAt=$now
            WHERE RunId=$run AND State IN ('Failed','Partial','Cancelled')
              AND AttemptCount < (SELECT MaxAttempts FROM AgentRun WHERE Id=$run)
            """, ("$run", runId), ("$now", Now(now)));
        stages.Transaction = transaction;
        if (stages.ExecuteNonQuery() == 0) throw new InvalidOperationException("JOB_NOT_REQUEUEABLE");
        using var run = LocalDatabase.Command(connection, "UPDATE AgentRun SET State='Queued',Error=NULL,LeaseOwner=NULL,LeaseExpiresAt=NULL,UpdatedAt=$now WHERE Id=$run AND State<>'Completed'", ("$run", runId), ("$now", Now(now)));
        run.Transaction = transaction;
        if (run.ExecuteNonQuery() != 1) throw new KeyNotFoundException("RUN_NOT_FOUND");
        IncrementRevision(connection, transaction);
        AppendEvent(connection, transaction, runId, "job_requeued", null, null, ResearchStore.Serialize(new { state = "Queued" }), now);
        transaction.Commit();
    }

    public void CompleteRun(string runId, string ownerSession, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var check = LocalDatabase.Command(connection, "SELECT COUNT(*) FROM JobStage WHERE RunId=$run AND State<>'Completed'", ("$run", runId));
        check.Transaction = transaction;
        if (Convert.ToInt32(check.ExecuteScalar()) != 0) throw new InvalidOperationException("JOB_STAGES_INCOMPLETE");
        using var update = LocalDatabase.Command(connection, "UPDATE AgentRun SET State='Completed',Stage='Done',LeaseOwner=NULL,LeaseExpiresAt=NULL,Error=NULL,UpdatedAt=$now WHERE Id=$run AND (LeaseOwner=$owner OR LeaseOwner IS NULL)", ("$now", Now(now)), ("$run", runId), ("$owner", ownerSession));
        update.Transaction = transaction;
        if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("STALE_JOB_LEASE");
        IncrementRevision(connection, transaction);
        AppendEvent(connection, transaction, runId, "job_completed", null, null, ResearchStore.Serialize(new { state = "Completed" }), now);
        transaction.Commit();
    }

    public int RecoverExpiredLeases(DateTimeOffset now)
        => RecoverLeases(now, expiredOnly: true);

    public int RecoverStaleLeases(DateTimeOffset now)
        => RecoverLeases(now, expiredOnly: false);

    private int RecoverLeases(DateTimeOffset now, bool expiredOnly)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var expiry = expiredOnly ? " AND JobStage.LeaseExpiresAt <= $now" : "";
        using var find = LocalDatabase.Command(connection, "SELECT Id,RunId,StageId,StageKey,OwnerSession FROM JobAttempt WHERE State='Running' AND Id IN (SELECT AttemptId FROM AgentRunEvent WHERE AttemptId IS NOT NULL) AND EXISTS (SELECT 1 FROM JobStage WHERE JobStage.Id=JobAttempt.StageId AND JobStage.State='Running'" + expiry + ")", ("$now", Now(now)));
        find.Transaction = transaction;
        using var reader = find.ExecuteReader();
        var expired = new List<(string AttemptId, string RunId, string StageId, string StageKey, string Owner)>();
        while (reader.Read()) expired.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        reader.Close();
        foreach (var item in expired)
        {
            using (var attempt = LocalDatabase.Command(connection, "UPDATE JobAttempt SET State='Interrupted',FinishedAt=$now,Error='PROCESS_INTERRUPTED',LastSequence=LastSequence+1 WHERE Id=$id AND State='Running'", ("$now", Now(now)), ("$id", item.AttemptId)))
            { attempt.Transaction = transaction; attempt.ExecuteNonQuery(); }
            using (var stage = LocalDatabase.Command(connection, "UPDATE JobStage SET State='Queued',Error='PROCESS_INTERRUPTED',LeaseOwner=NULL,LeaseExpiresAt=NULL,UpdatedAt=$now WHERE Id=$id AND State='Running'", ("$now", Now(now)), ("$id", item.StageId)))
            { stage.Transaction = transaction; stage.ExecuteNonQuery(); }
            using (var run = LocalDatabase.Command(connection, "UPDATE AgentRun SET State='Queued',Stage=$stage,LeaseOwner=NULL,LeaseExpiresAt=NULL,Error='PROCESS_INTERRUPTED',UpdatedAt=$now WHERE Id=$run AND LeaseOwner=$owner", ("$stage", item.StageKey), ("$now", Now(now)), ("$run", item.RunId), ("$owner", item.Owner)))
            { run.Transaction = transaction; run.ExecuteNonQuery(); }
            AppendEvent(connection, transaction, item.RunId, "attempt_interrupted", item.AttemptId, 2, ResearchStore.Serialize(new { stage_key = item.StageKey, error = "PROCESS_INTERRUPTED" }), now);
        }
        if (expired.Count > 0) IncrementRevision(connection, transaction);
        transaction.Commit();
        return expired.Count;
    }

    public bool TryConsumeBudget(string runId, int toolCalls, int modelRequests)
    {
        if (toolCalls < 0 || modelRequests < 0 || toolCalls == 0 && modelRequests == 0) throw new ArgumentException("INVALID_BUDGET_USAGE");
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var update = LocalDatabase.Command(connection, """
            UPDATE JobBudget SET UsedToolCalls=UsedToolCalls+$tools,UsedModelRequests=UsedModelRequests+$models
            WHERE RunId=(SELECT RootRunId FROM JobBudget WHERE RunId=$run)
              AND UsedToolCalls+$tools <= MaxToolCalls AND UsedModelRequests+$models <= MaxModelRequests
            """, ("$tools", toolCalls), ("$models", modelRequests), ("$run", runId));
        update.Transaction = transaction;
        if (update.ExecuteNonQuery() == 0) { transaction.Rollback(); return false; }
        IncrementRevision(connection, transaction);
        transaction.Commit();
        return true;
    }

    private static string RootRunId(SqliteConnection connection, SqliteTransaction transaction, string runId)
    {
        using var command = LocalDatabase.Command(connection, "SELECT COALESCE((SELECT RootRunId FROM JobBudget WHERE RunId=$run),$run)", ("$run", runId));
        command.Transaction = transaction;
        return Convert.ToString(command.ExecuteScalar())!;
    }

    private static void EnsureLease(SqliteConnection connection, SqliteTransaction transaction, JobLease lease, DateTimeOffset now)
    {
        using var command = LocalDatabase.Command(connection, "SELECT COUNT(*) FROM JobStage WHERE Id=$id AND RunId=$run AND State='Running' AND LeaseOwner=$owner AND LeaseExpiresAt > $now", ("$id", lease.StageId), ("$run", lease.RunId), ("$owner", lease.OwnerSession), ("$now", Now(now)));
        command.Transaction = transaction;
        if (Convert.ToInt32(command.ExecuteScalar()) != 1) throw new InvalidOperationException("STALE_JOB_LEASE");
    }

    private void FinishStage(JobLease lease, string error, bool retryable, bool cancelled, DateTimeOffset now, TimeSpan retryDelay)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        EnsureLease(connection, transaction, lease, now);
        var maxAttempts = MaxAttempts(connection, transaction, lease.RunId);
        var nextState = cancelled ? "Cancelled" : retryable && lease.AttemptNumber < maxAttempts ? "RetryScheduled" : "Failed";
        var runState = cancelled ? "Cancelled" : nextState == "RetryScheduled" ? "Queued" : "Failed";
        var next = nextState == "RetryScheduled" ? now.Add(retryDelay) : (DateTimeOffset?)null;
        using (var stage = LocalDatabase.Command(connection, "UPDATE JobStage SET State=$state,Error=$error,NextAttemptAt=$next,LeaseOwner=NULL,LeaseExpiresAt=NULL,UpdatedAt=$now WHERE Id=$id AND State='Running' AND LeaseOwner=$owner", ("$state", nextState), ("$error", error), ("$next", next is null ? null : Now(next.Value)), ("$now", Now(now)), ("$id", lease.StageId), ("$owner", lease.OwnerSession)))
        { stage.Transaction = transaction; if (stage.ExecuteNonQuery() != 1) throw new InvalidOperationException("STALE_JOB_LEASE"); }
        FinishAttempt(connection, transaction, lease, cancelled ? "Cancelled" : "Failed", error, 2, now);
        using (var run = LocalDatabase.Command(connection, "UPDATE AgentRun SET State=$state,Stage=$stage,LeaseOwner=NULL,LeaseExpiresAt=NULL,Error=$error,UpdatedAt=$now WHERE Id=$run AND LeaseOwner=$owner", ("$state", runState), ("$stage", lease.StageKey), ("$error", error), ("$now", Now(now)), ("$run", lease.RunId), ("$owner", lease.OwnerSession)))
        { run.Transaction = transaction; run.ExecuteNonQuery(); }
        IncrementRevision(connection, transaction);
        AppendEvent(connection, transaction, lease.RunId, nextState == "RetryScheduled" ? "stage_retry_scheduled" : "stage_failed", lease.AttemptId, 2, ResearchStore.Serialize(new { stage_key = lease.StageKey, state = nextState, error }), now);
        transaction.Commit();
    }

    private static int MaxAttempts(SqliteConnection connection, SqliteTransaction transaction, string runId)
    {
        using var command = LocalDatabase.Command(connection, "SELECT MaxAttempts FROM AgentRun WHERE Id=$run", ("$run", runId));
        command.Transaction = transaction;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void FinishAttempt(SqliteConnection connection, SqliteTransaction transaction, JobLease lease, string state, string? error, int sequence, DateTimeOffset now)
    {
        using var attempt = LocalDatabase.Command(connection, "UPDATE JobAttempt SET State=$state,FinishedAt=$now,LastSequence=$sequence,Error=$error WHERE Id=$id AND State='Running'", ("$state", state), ("$now", Now(now)), ("$sequence", sequence), ("$error", error), ("$id", lease.AttemptId));
        attempt.Transaction = transaction;
        if (attempt.ExecuteNonQuery() != 1) throw new InvalidOperationException("STALE_JOB_ATTEMPT");
    }

    private static void AppendEvent(SqliteConnection connection, SqliteTransaction transaction, string runId, string type, string? attemptId, int? attemptSequence, string payload, DateTimeOffset now)
    {
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO AgentRunEvent(RunId,Sequence,Type,PayloadJson,CreatedAt,AttemptId,AttemptSequence)
            SELECT $run,COALESCE(MAX(Sequence),0)+1,$type,$payload,$now,$attempt,$attemptSequence FROM AgentRunEvent WHERE RunId=$run
            """, ("$run", runId), ("$type", type), ("$payload", payload), ("$now", Now(now)), ("$attempt", attemptId), ("$attemptSequence", attemptSequence));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private static void IncrementRevision(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "UPDATE WorkspaceMeta SET DataRevision=DataRevision+1 WHERE Id=1";
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("WORKSPACE_META_MISSING");
    }

    private static JobRunRecord ReadRun(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetInt32(11), reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13)), DateTimeOffset.Parse(reader.GetString(14)), DateTimeOffset.Parse(reader.GetString(15)));

    private static JobStageRecord ReadStage(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.GetInt32(5),
        reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)), reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11)),
        reader.GetString(12), reader.GetString(13), DateTimeOffset.Parse(reader.GetString(14)), DateTimeOffset.Parse(reader.GetString(15)));
}
