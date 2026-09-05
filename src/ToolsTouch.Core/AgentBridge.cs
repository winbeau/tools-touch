using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ToolsTouch.Core;

public interface IAgentBridge
{
    event Action<JsonElement>? EventReceived;
    void SetToolContext(string invocation, ToolContext context);
    void RemoveToolContext(string invocation);
    Task<JsonElement> RequestAsync(object command, CancellationToken cancellationToken = default);
    Task CancelAsync();
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
        var input = JsonSerializer.SerializeToElement(command, ResearchStore.Json);
        var id = input.GetProperty("id").GetString()!;
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion)) throw new InvalidOperationException("DUPLICATE_REQUEST_ID");
        try
        {
            await WriteAsync(input, cancellationToken);
            var response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            if (response.TryGetProperty("ok", out var ok) && !ok.GetBoolean())
                throw new InvalidOperationException(response.TryGetProperty("code", out var code) ? code.GetString() : "AGENT_REQUEST_FAILED");
            return response;
        }
        finally { pending.TryRemove(id, out _); }
    }

    public async Task CancelAsync()
    {
        await toolCancellation.CancelAsync();
        await RequestAsync(new { type = "cancel", id = Guid.NewGuid().ToString("N") });
    }

    private async Task PumpAsync()
    {
        try
        {
            // Pipe readers must not capture WPF's dispatcher: App.OnExit waits for them
            // while disposing the bridge with the UI thread blocked.
            while (await process.StandardOutput.ReadLineAsync(lifetime.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Length > 2_000_000) throw new InvalidOperationException("AGENT_FRAME_TOO_LARGE");
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement.Clone();
                var type = message.GetProperty("type").GetString();
                if (type == "ready")
                {
                    var allowed = message.GetProperty("tools").EnumerateArray().Select(value => value.GetString()).Order().ToArray();
                    if (!allowed.SequenceEqual(ToolDispatcher.AllowedTools.Order())) throw new InvalidOperationException("AGENT_TOOL_MISMATCH");
                    ready.TrySetResult();
                    diagnostics?.Write("component", "ready");
                }
                else if (type == "response" && message.TryGetProperty("id", out var requestId))
                    pending.GetValueOrDefault(requestId.GetString()!)?.TrySetResult(message);
                else if (type == "tool_request")
                    _ = ReplyToToolAsync(message, toolCancellation.Token);
                else
                {
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
            EventReceived?.Invoke(JsonSerializer.SerializeToElement(new { type = "host_disconnected", code = "AGENT_PROTOCOL_FAILED" }));
        }
        finally
        {
            if (!lifetime.IsCancellationRequested && !process.HasExited) process.Kill(entireProcessTree: true);
            var error = new InvalidOperationException("AGENT_DISCONNECTED");
            ready.TrySetException(error);
            foreach (var completion in pending.Values) completion.TrySetException(error);
            if (!lifetime.IsCancellationRequested)
                EventReceived?.Invoke(JsonSerializer.SerializeToElement(new { type = "host_disconnected", code = "AGENT_DISCONNECTED" }));
        }
    }

    private async Task ReplyToToolAsync(JsonElement message, CancellationToken cancellationToken)
    {
        try
        {
            var id = message.GetProperty("id").GetString()!;
            var invocation = message.GetProperty("run_id").GetString()!;
            var result = contexts.TryGetValue(invocation, out var context)
                ? await dispatcher.ExecuteAsync(message.GetProperty("tool").GetString()!, message.GetProperty("arguments"), id, cancellationToken, context)
                : (object)new { ok = false, error = new { code = "UNKNOWN_TASK_CONTEXT" } };
            await WriteAsync(new { type = "tool_result", id, result }, lifetime.Token);
        }
        catch (Exception) when (lifetime.IsCancellationRequested || process.HasExited) { }
        catch { await toolCancellation.CancelAsync(); }
    }

    private async Task WriteAsync(object value, CancellationToken cancellationToken)
    {
        await writer.WaitAsync(cancellationToken);
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value, ResearchStore.Json).AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
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
}
