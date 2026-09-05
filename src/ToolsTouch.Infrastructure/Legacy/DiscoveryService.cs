using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using ToolsTouch.Application;

namespace ToolsTouch.Core;

public sealed class DiscoveryService(LocalDatabase database, ResearchStore store, LibraryService library, IAgentBridge bridge, JobScheduler? scheduler = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string ownerSession = Guid.NewGuid().ToString("N");
    public event Action? Changed;

    public AgentRun Create(string kind, string request, string? professorId = null, int maxToolCalls = 30, int timeoutMs = 180000,
        string? provider = null, string? model = null)
    {
        if (kind is not ("Discover" or "Analyze" or "Draft")) throw new ArgumentException("INVALID_TASK_KIND");
        if (kind != "Discover") _ = store.GetProfessor(professorId ?? throw new ArgumentException("PROFESSOR_REQUIRED"));
        if (maxToolCalls is < 1 or > 100 || timeoutMs is < 1000 or > 900000) throw new ArgumentException("INVALID_BUDGET");
        var inputJson = ResearchStore.Serialize(new { request, professorId, profileId = library.GetProfile(confirmedOnly: true)?.Id });
        var budgetJson = ResearchStore.Serialize(new { maxToolCalls, timeoutMs });
        if (scheduler is null) return store.CreateRun(kind, inputJson, budgetJson);
        var stageKeys = kind switch { "Discover" => new[] { "Research" }, "Analyze" => new[] { "Read", "Analyze" }, "Draft" => new[] { "Draft" }, _ => throw new InvalidOperationException("INVALID_TASK_KIND") };
        var stages = stageKeys.Select((stage, ordinal) => new JobStageDefinition(stage, ordinal,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(inputJson + "\0" + stage))).ToLowerInvariant(), kind + ":" + stage)).ToArray();
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model)) throw new InvalidOperationException("MODEL_CONFIGURATION_REQUIRED");
        var job = scheduler.Create(new JobDefinition(kind, inputJson, budgetJson, provider, model,
            new JobBudget(maxToolCalls, Math.Max(stageKeys.Length, stageKeys.Length * 3), checked(timeoutMs * stageKeys.Length)), stages));
        return store.GetRun(job.Id);
    }

    public async Task ExecuteAsync(string runId, string model, CancellationToken cancellationToken = default, string provider = "openai-codex")
    {
        if (!await gate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("RUN_BUSY");
        var mayOwnActiveRun = false;
        JobLease? currentLease = null;
        try
        {
            var run = store.GetRun(runId);
            if (run.State == "Completed") throw new InvalidOperationException("RUN_ALREADY_COMPLETED");
            if (scheduler is not null)
            {
                var scheduled = scheduler.GetRun(runId);
                if (!string.IsNullOrWhiteSpace(scheduled.Provider) && !string.Equals(provider, scheduled.Provider, StringComparison.Ordinal))
                    throw new InvalidOperationException("JOB_PROVIDER_CHANGED");
                if (!string.IsNullOrWhiteSpace(scheduled.Model) && !string.Equals(model, scheduled.Model, StringComparison.Ordinal))
                    throw new InvalidOperationException("JOB_MODEL_CHANGED");
                provider = scheduled.Provider ?? provider;
                model = scheduled.Model ?? model;
            }
            if (scheduler is not null && run.State is ("Failed" or "Partial" or "Cancelled")) scheduler.RequeueIncomplete(runId);
            var input = JsonSerializer.Deserialize<JsonElement>(run.InputJson);
            var budget = JsonSerializer.Deserialize<JsonElement>(run.BudgetJson);
            var completed = run.CheckpointJson == null ? new Dictionary<string, JsonElement>() :
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(run.CheckpointJson)!;
            var stages = run.Kind switch
            {
                "Discover" => new[] { "Research" }, "Analyze" => ["Read", "Analyze"], "Draft" => ["Draft"],
                _ => throw new InvalidOperationException("INVALID_TASK_KIND")
            };
            foreach (var stage in stages)
            {
                if (completed.ContainsKey(stage)) continue;
                cancellationToken.ThrowIfCancellationRequested();
                currentLease = scheduler?.AcquireNext(runId, ownerSession, TimeSpan.FromMilliseconds(budget.GetProperty("timeoutMs").GetInt32() + 15000));
                if (scheduler is not null && (currentLease is null || currentLease.StageKey != stage)) throw new InvalidOperationException("JOB_STAGE_NOT_READY");
                if (scheduler is null) store.UpdateRun(runId, "Running", stage);
                Changed?.Invoke();
                var attemptId = currentLease?.AttemptId ?? Guid.NewGuid().ToString("N");
                var invocation = runId + ":" + stage + ":" + attemptId;
                var policyId = stage == "Analyze" ? "analysis" : stage == "Draft" ? "draft" : "research";
                bridge.SetToolContext(invocation, new(run.Kind, input.GetProperty("profileId").GetString(), input.GetProperty("professorId").GetString(), run.Id, policyId));
                var result = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnEvent(JsonElement message)
                {
                    if (message.GetProperty("type").GetString() == "host_disconnected")
                    { result.TrySetException(new InvalidOperationException("AGENT_DISCONNECTED")); return; }
                    if (!message.TryGetProperty("run_id", out var eventRun) || eventRun.GetString() != runId ||
                        !message.TryGetProperty("stage_key", out var eventStage) || eventStage.GetString() != stage ||
                        !message.TryGetProperty("attempt_id", out var eventAttempt) || eventAttempt.GetString() != attemptId) return;
                    store.AppendEvent(runId, message.GetProperty("type").GetString()!, message.GetRawText());
                    Changed?.Invoke();
                    if (message.GetProperty("type").GetString() == "run_finished") result.TrySetResult(message.GetProperty("data").Clone());
                }
                bridge.EventReceived += OnEvent;
                try
                {
                    mayOwnActiveRun = true;
                    if (scheduler is not null && !scheduler.ConsumeBudget(runId, modelRequests: 1)) throw new InvalidOperationException("JOB_MODEL_BUDGET_EXCEEDED");
                    try { await bridge.RequestAsync(new
                    {
                        type = "run", id = Guid.NewGuid().ToString("N"), run_id = runId, stage_key = stage, attempt_id = attemptId, model, provider,
                        policy_id = policyId,
                        input_context = new { prompt = BuildPrompt(run.Kind, stage, input, completed), output_kind = stage == "Analyze" ? "analysis" : stage == "Draft" ? "draft" : "research" },
                        limits = new { max_tool_calls = budget.GetProperty("maxToolCalls").GetInt32(), timeout_ms = budget.GetProperty("timeoutMs").GetInt32() }
                    }, cancellationToken); }
                    catch (InvalidOperationException error) when (error.Message is "RUN_BUSY" or "AUTH_IN_PROGRESS" or "MODEL_NOT_FOUND" or "SESSION_START_FAILED")
                    { mayOwnActiveRun = false; throw; }
                    using var registration = cancellationToken.Register(() => { _ = CancelQuietlyAsync(); });
                    var terminal = await result.Task.WaitAsync(TimeSpan.FromMilliseconds(budget.GetProperty("timeoutMs").GetInt32() + 15000), cancellationToken);
                    mayOwnActiveRun = false;
                    if (terminal.GetProperty("state").GetString() != "Completed")
                        throw new InvalidOperationException(terminal.TryGetProperty("code", out var code) ? code.GetString() : "STAGE_FAILED");
                    var output = terminal.GetProperty("output").Clone();
                    var saveResult = ValidateOutput(run, stage, input, output, model);
                    completed.Add(stage, output);
                    if (scheduler is null) store.UpdateRun(runId, "Running", stage, ResearchStore.Serialize(completed), saveStageResult: saveResult);
                    else
                    {
                        var commit = stage == "Analyze"
                            ? new JobStageCommit(output.GetRawText(), input.GetProperty("professorId").GetString(), input.GetProperty("profileId").GetString(), "openai-codex/" + model)
                            : null;
                        scheduler.Complete(currentLease!, output.GetRawText(), ResearchStore.Serialize(completed), commit);
                        currentLease = null;
                    }
                }
                finally { bridge.EventReceived -= OnEvent; bridge.RemoveToolContext(invocation); }
            }
            if (scheduler is null) store.UpdateRun(runId, "Completed", "Done", ResearchStore.Serialize(completed));
            else scheduler.CompleteRun(runId, ownerSession);
        }
        catch (Exception error)
        {
            if (mayOwnActiveRun) await CancelQuietlyAsync();
            if (scheduler is not null && currentLease is not null)
            {
                try
                {
                    if (cancellationToken.IsCancellationRequested) scheduler.Cancel(currentLease, "CANCELLED");
                    else scheduler.Fail(currentLease, error is InvalidOperationException ? error.Message : "TASK_FAILED", IsRetryable(error));
                }
                finally { currentLease = null; }
            }
            var run = store.GetRun(runId);
            if (scheduler is null && run.State != "Completed")
                store.UpdateRun(runId, cancellationToken.IsCancellationRequested ? "Cancelled" : run.CheckpointJson != null ? "Partial" : "Failed", run.Stage,
                    error: error is InvalidOperationException ? error.Message : error is TimeoutException ? "AGENT_TIMEOUT" : "TASK_FAILED");
            throw;
        }
        finally { gate.Release(); Changed?.Invoke(); }
    }

    private async Task CancelQuietlyAsync() { try { await bridge.CancelAsync(); } catch { } }
    private static bool IsRetryable(Exception error) => error is IOException or HttpRequestException or TimeoutException || error is InvalidOperationException invalid && invalid.Message is "AGENT_DISCONNECTED" or "AGENT_TIMEOUT" or "RUN_BUSY";

    private string BuildPrompt(string kind, string stage, JsonElement input, Dictionary<string, JsonElement> completed)
    {
        var request = input.GetProperty("request").GetString();
        if (kind == "Discover")
            return $"Find and verify professors matching this user request: {request}. Search local professors first, then public web sources. " +
                "Fetch institutional or personal pages before saving; preserve evidence URLs and only published emails. Aim for up to five verified candidates. " +
                "Do not generate drafts. Return saved professor IDs and collected paper IDs; if no verified candidates exist, return empty IDs and explain missing sources.";
        var professor = store.GetProfessor(input.GetProperty("professorId").GetString()!);
        var context = "Target professor: " + ResearchStore.Serialize(professor) + "\nUser request: " + request + "\n";
        if (stage == "Read")
            return context + "Fetch target homepage, search relevant recent papers and read up to three papers using read_paper. " +
                "Distinguish abstract-only results from full text. Read confirmed user profile; do not make personal matching claims if missing. Return target ID and actual paper IDs.";
        if (stage == "Analyze")
            return context + "Previous research checkpoint: " + ResearchStore.Serialize(completed) +
                "\nUse tools to read paper evidence and confirmed profile as needed. Summarize research with source evidence. " +
                "Set personal_match to null if no confirmed profile exists. Never invent recruitment status or treat relevance as admission probability.";
        return context + "Latest saved analysis: " + LatestAnalysis(professor.Id) +
            "\nRead confirmed user profile. Create exactly one local outreach draft using the supplied tool, with source evidence. " +
            "Do not invent user experiences. Do not claim to have read full text when only abstracts are available. Return the created draft_id.";
    }

    public string? LatestAnalysis(string professorId)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection,
            "SELECT ContentJson FROM ProfessorAnalysis WHERE ProfessorId=$id ORDER BY CreatedAt DESC LIMIT 1", ("$id", professorId));
        return command.ExecuteScalar() as string;
    }

    private Action<SqliteConnection, SqliteTransaction>? ValidateOutput(AgentRun run, string stage, JsonElement input, JsonElement output, string model)
    {
        if (stage == "Draft")
        {
            var draft = new OutreachService(database).Get(output.GetProperty("draft_id").GetString()!);
            if (draft.ProfessorId != input.GetProperty("professorId").GetString()) throw new InvalidOperationException("WRONG_DRAFT_TARGET");
            if (new OutreachService(database).FindByIdempotencyKey("task:" + run.Id + ":draft")?.Id != draft.Id)
                throw new InvalidOperationException("DRAFT_NOT_CREATED_BY_TASK");
            return null;
        }
        foreach (var id in output.GetProperty("paper_ids").EnumerateArray()) _ = library.GetPaper(id.GetString()!);
        if (stage != "Analyze")
        {
            var ids = output.GetProperty("professor_ids").EnumerateArray().ToArray();
            foreach (var id in ids) _ = store.GetProfessor(id.GetString()!);
            if (run.Kind == "Discover" && ids.Length == 0) throw new InvalidOperationException("NO_VERIFIED_CANDIDATES");
            return null;
        }
        var professorId = input.GetProperty("professorId").GetString()!;
        if (output.GetProperty("professor_id").GetString() != professorId) throw new InvalidOperationException("WRONG_ANALYSIS_TARGET");
        var profileId = input.GetProperty("profileId").GetString();
        if (profileId == null && output.GetProperty("personal_match").ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException("PROFILE_REQUIRED_FOR_MATCH");
        foreach (var evidence in output.GetProperty("evidence").EnumerateArray())
        {
            var url = evidence.GetProperty("url").GetString()!;
            if (library.SourceText(url) == null && !library.ListPapers().Any(paper => paper.SourceUrl == url)) throw new InvalidOperationException("UNVERIFIED_ANALYSIS_SOURCE");
        }
        return (connection, transaction) =>
        {
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO ProfessorAnalysis(Id,ProfessorId,ProfileId,ContentJson,Model,CreatedAt)
            VALUES($id,$prof,$profile,$content,$model,$now) ON CONFLICT(Id) DO UPDATE SET
            ContentJson=excluded.ContentJson,Model=excluded.Model,ProfileId=excluded.ProfileId,CreatedAt=excluded.CreatedAt
            """, ("$id", run.Id), ("$prof", professorId), ("$profile", profileId), ("$content", output.GetRawText()),
            ("$model", "openai-codex/" + model), ("$now", DateTimeOffset.UtcNow.ToString("O")));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
        };
    }
}
