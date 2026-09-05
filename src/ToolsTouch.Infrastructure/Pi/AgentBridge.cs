using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ToolsTouch.Application;

namespace ToolsTouch.Core;

public interface IAgentBridge
{
    event Action<JsonElement>? EventReceived;
    void SetToolContext(string invocation, ToolContext context);
    void RemoveToolContext(string invocation);
    Task<JsonElement> RequestAsync(object command, CancellationToken cancellationToken = default);
    Task CancelAsync();
    Task CancelAsync(string runId, string stageKey, string attemptId) => CancelAsync();
}

public sealed class AgentBridge : IAgentBridge, IAsyncDisposable
{
    private readonly Process process;
    private readonly ToolDispatcher dispatcher;
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> pending = new();
    private readonly ConcurrentDictionary<string, ToolContext> contexts = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task pump;
    private readonly Task errors;
    private CancellationTokenSource toolCancellation = new();
    private readonly DiagnosticLog? diagnostics;
    private string? activeAuthRequestId;
    private RunTarget? activeRun;
    public event Action<JsonElement>? EventReceived;
    public void SetToolContext(string invocation, ToolContext context) => contexts[invocation] = context;
    public void RemoveToolContext(string invocation) => contexts.TryRemove(invocation, out _);

    public AgentBridge(string nodePath, string hostPath, string piDirectory, ToolDispatcher dispatcher, DiagnosticLog? diagnostics = null)
    {
        this.diagnostics = diagnostics;
        this.dispatcher = dispatcher;
        var start = new ProcessStartInfo(nodePath)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(hostPath))!,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add(Path.GetFullPath(hostPath));
        start.ArgumentList.Add(Path.GetFullPath(piDirectory));
        // Child receives OS necessities, not inherited API keys, Gmail credentials, NODE_OPTIONS or extension configuration.
        var keep = new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "HOME", "USERPROFILE", "LOCALAPPDATA", "APPDATA" };
        var environment = start.Environment.Where(pair => keep.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)).ToArray();
        start.Environment.Clear();
        foreach (var pair in environment) start.Environment[pair.Key] = pair.Value;
        foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY" })
        {
            var value = Environment.GetEnvironmentVariable(key.ToLowerInvariant()) ?? Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value)) start.Environment[key] = value;
        }
        AgentProxy.Configure(start.Environment, HttpClient.DefaultProxy);
        process = Process.Start(start) ?? throw new InvalidOperationException("AGENT_START_FAILED");
        diagnostics?.Write("component", "process_started");
        pump = PumpAsync();
        // Drain stderr to prevent pipe deadlock, without exposing raw provider diagnostics or secrets to UI/model.
        errors = DrainErrorsAsync();
    }

    public async Task WaitReadyAsync(CancellationToken cancellationToken = default) =>
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(90), cancellationToken);

    public async Task<JsonElement> RequestAsync(object command, CancellationToken cancellationToken = default)
    {
        await WaitReadyAsync(cancellationToken);
        var input = AgentProtocol.PrepareRequest(command);
        var id = input.GetProperty("id").GetString()!;
        var type = input.GetProperty("type").GetString();
        if (type == "login") activeAuthRequestId = id;
        if (type == "run") activeRun = new(input.GetProperty("run_id").GetString()!, input.GetProperty("stage_key").GetString()!, input.GetProperty("attempt_id").GetString()!);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion)) throw new InvalidOperationException("DUPLICATE_REQUEST_ID");
        try
        {
            await WriteAsync(input, cancellationToken);
            var response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            if (response.TryGetProperty("ok", out var ok) && !ok.GetBoolean())
            {
                if (type == "login") activeAuthRequestId = null;
                if (type == "run") activeRun = null;
                throw new InvalidOperationException(response.TryGetProperty("code", out var code) ? code.GetString() : "AGENT_REQUEST_FAILED");
            }
            return response;
        }
        finally { pending.TryRemove(id, out _); }
    }

    public async Task CancelAsync()
    {
        await toolCancellation.CancelAsync();
        if (activeRun is { } run)
            await CancelAsync(run.RunId, run.StageKey, run.AttemptId);
        else if (activeAuthRequestId is { } auth)
            await RequestAsync(new { type = "cancel_auth", id = Guid.NewGuid().ToString("N"), auth_request_id = auth });
    }

    public async Task CancelAsync(string runId, string stageKey, string attemptId)
    {
        await toolCancellation.CancelAsync();
        await RequestAsync(new { type = "cancel_run", id = Guid.NewGuid().ToString("N"), run_id = runId, stage_key = stageKey, attempt_id = attemptId });
    }

    private async Task PumpAsync()
    {
        try
        {
            // Pipe readers must not capture WPF's dispatcher: App.OnExit waits for them
            // while disposing the bridge with the UI thread blocked.
            while (await AgentProtocol.ReadFrameAsync(process.StandardOutput.BaseStream, lifetime.Token).ConfigureAwait(false) is { } message)
            {
                AgentProtocol.ValidateMessage(message);
                var type = message.GetProperty("type").GetString();
                if (type == "ready")
                {
                    var policies = message.GetProperty("supported_policies").EnumerateArray().Select(value => value.GetString()).Where(value => value != null).ToArray();
                    if (!new[] { "research", "analysis", "semantic", "draft" }.All(policies.Contains)) throw new InvalidOperationException("AGENT_POLICY_MISMATCH");
                    ready.TrySetResult();
                    diagnostics?.Write("component", "ready");
                }
                else if (type == "response" && message.TryGetProperty("id", out var requestId))
                    pending.GetValueOrDefault(requestId.GetString()!)?.TrySetResult(message);
                else if (type == "tool_request")
                    _ = ReplyToToolAsync(message, toolCancellation.Token);
                else
                {
                    if (type == "auth_finished") activeAuthRequestId = null;
                    if (type == "run_finished") activeRun = null;
                    if (type == "run_started")
                    {
                        toolCancellation.Dispose();
                        toolCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    }
                    EventReceived?.Invoke(message);
                }
            }
        }
        catch (Exception error) when (!lifetime.IsCancellationRequested)
        {
            diagnostics?.Write("component", "protocol_failed", "AGENT_PROTOCOL_FAILED", error);
            EventReceived?.Invoke(JsonSerializer.SerializeToElement(new { protocol_version = AgentProtocol.Version, type = "host_disconnected", code = "AGENT_PROTOCOL_FAILED" }));
        }
        finally
        {
            if (!lifetime.IsCancellationRequested && !process.HasExited) process.Kill(entireProcessTree: true);
            var error = new InvalidOperationException("AGENT_DISCONNECTED");
            ready.TrySetException(error);
            foreach (var completion in pending.Values) completion.TrySetException(error);
            if (!lifetime.IsCancellationRequested)
                EventReceived?.Invoke(JsonSerializer.SerializeToElement(new { protocol_version = AgentProtocol.Version, type = "host_disconnected", code = "AGENT_DISCONNECTED" }));
        }
    }

    private async Task ReplyToToolAsync(JsonElement message, CancellationToken cancellationToken)
    {
        try
        {
            var id = message.GetProperty("tool_call_id").GetString()!;
            var runId = message.GetProperty("run_id").GetString()!;
            var stageKey = message.GetProperty("stage_key").GetString()!;
            var attemptId = message.GetProperty("attempt_id").GetString()!;
            var invocation = runId + ":" + stageKey + ":" + attemptId;
            var result = contexts.TryGetValue(invocation, out var context)
                ? await dispatcher.ExecuteAsync(message.GetProperty("tool").GetString()!, message.GetProperty("arguments"), id, cancellationToken, context)
                : (object)new { ok = false, error = new { code = "UNKNOWN_TASK_CONTEXT" } };
            await WriteAsync(new { type = "tool_result", id, run_id = runId, stage_key = stageKey, attempt_id = attemptId, tool_call_id = id, result }, lifetime.Token);
        }
        catch (Exception) when (lifetime.IsCancellationRequested || process.HasExited) { }
        catch { await toolCancellation.CancelAsync(); }
    }

    private async Task WriteAsync(object value, CancellationToken cancellationToken)
    {
        await writer.WaitAsync(cancellationToken);
        try
        {
            var frame = AgentProtocol.Encode(AgentProtocol.PrepareRequest(value));
            await process.StandardInput.BaseStream.WriteAsync(frame.AsMemory(), cancellationToken);
            await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
        }
        finally { writer.Release(); }
    }

    private async Task DrainErrorsAsync()
    {
        try
        {
            var reported = false;
            while (await process.StandardError.ReadLineAsync(lifetime.Token).ConfigureAwait(false) != null)
            {
                if (!reported) diagnostics?.Write("component", "stderr_received", "COMPONENT_DIAGNOSTIC_AVAILABLE");
                reported = true;
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        await toolCancellation.CancelAsync();
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        try { await Task.WhenAll(pump, errors); } catch (OperationCanceledException) { }
        process.Dispose();
        lifetime.Dispose();
        // In-flight tool completions retain their cancellation token; their replies are suppressed after shutdown.
    }

    private sealed record RunTarget(string RunId, string StageKey, string AttemptId);
}
