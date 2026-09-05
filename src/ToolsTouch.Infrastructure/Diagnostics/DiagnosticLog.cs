using System.Text.Json;
using System.Text.RegularExpressions;

namespace ToolsTouch.Core;

// Deliberately logs structured metadata only, never arbitrary exception messages,
// request/response bodies, URLs, credential values, auth codes or user documents.
public sealed class DiagnosticLog(string dataDirectory)
{
    private static readonly HashSet<string> KnownErrors = new(StringComparer.Ordinal)
    {
        "RUN_BUSY", "AUTH_IN_PROGRESS", "AUTH_CANCELLED", "AUTH_FAILED", "AUTH_CALLBACK_PORT_BUSY", "AUTH_STATE_MISMATCH",
        "AUTH_CREDENTIAL_SAVE_FAILED", "AUTH_NETWORK_FAILED", "AUTH_REJECTED", "AUTH_METHOD_UNSUPPORTED", "AUTH_PROMPT_EXPIRED", "MODEL_NOT_FOUND",
        "COMPONENT_FILES_MISSING", "AGENT_DISCONNECTED", "AGENT_PROTOCOL_FAILED", "AGENT_START_FAILED",
        "GMAIL_CLIENT_NOT_CONFIGURED", "GMAIL_CLIENT_FILE_MISSING", "GMAIL_CLIENT_INVALID", "GMAIL_CLIENT_NOT_DESKTOP",
        "GMAIL_CREDENTIAL_UNREADABLE", "GMAIL_AUTH_TIMEOUT", "GMAIL_AUTH_DENIED", "GMAIL_REQUIRED_SCOPES_MISSING",
        "GMAIL_REFRESH_TOKEN_REQUIRED", "GMAIL_OAUTH_INVALID_CLIENT", "GMAIL_OAUTH_INVALID_GRANT", "GMAIL_API_DISABLED",
        "GMAIL_AUTH_EXCHANGE_FAILED", "GMAIL_PROFILE_FAILED", "GMAIL_NOT_CONNECTED", "GMAIL_CLIENT_CHANGED_RECONNECT"
    };
    public static string ErrorCode(Exception error) => KnownErrors.Contains(error.Message) ? error.Message : error switch
    {
        OperationCanceledException => "CANCELLED", TimeoutException => "TIMEOUT", HttpRequestException => "NETWORK_FAILED",
        IOException => "IO_FAILED", UnauthorizedAccessException => "ACCESS_DENIED", JsonException => "JSON_INVALID", _ => "OPERATION_FAILED"
    };
    private readonly object gate = new();
    public string DirectoryPath { get; } = Path.Combine(Path.GetFullPath(dataDirectory), "logs");
    public void Write(string area, string action, string? code = null, Exception? error = null)
    {
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, $"app-{DateTime.UtcNow:yyyyMMdd}.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Move(path, path + ".previous", true);
                File.AppendAllText(path, JsonSerializer.Serialize(new
                {
                    time = DateTimeOffset.UtcNow, area = Safe(area), action = Safe(action), code = Safe(code),
                    exception = error?.GetType().Name, hresult = error?.HResult,
                    cause = error?.InnerException?.GetType().Name
                }) + Environment.NewLine);
                foreach (var file in Directory.EnumerateFiles(DirectoryPath, "app-*.jsonl*"))
                    if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-14)) File.Delete(file);
            }
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public static string? Safe(string? value) => value == null ? null : Regex.IsMatch(value, "^[A-Za-z0-9_.:-]{1,100}$") ? value : "UNCLASSIFIED";
}
