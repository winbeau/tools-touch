using System.Buffers;
using System.Text;
using System.Text.Json;

namespace ToolsTouch.Application;

public static class AgentProtocol
{
    public const int Version = 2;
    public const int MaxFrameBytes = 1_048_576;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static JsonElement PrepareRequest(object command)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(command, Json));
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("INVALID_COMMAND");
        var fields = document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        fields.TryAdd("protocol_version", JsonSerializer.SerializeToElement(Version));
        var prepared = JsonSerializer.SerializeToElement(fields, Json);
        ValidateRequest(prepared);
        return prepared;
    }

    public static byte[] Encode(JsonElement message)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (json.Length > MaxFrameBytes) throw new InvalidOperationException("AGENT_FRAME_TOO_LARGE");
        var frame = new byte[json.Length + 1];
        json.CopyTo(frame, 0);
        frame[^1] = (byte)'\n';
        return frame;
    }

    public static byte[] Encode(object message) => Encode(JsonSerializer.SerializeToElement(message, Json));

    public static async Task<JsonElement?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var oneByte = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(oneByte.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (buffer.WrittenCount == 0) return null;
                throw new InvalidOperationException("AGENT_FRAME_TRUNCATED");
            }
            if (oneByte[0] == (byte)'\n')
            {
                var length = buffer.WrittenCount;
                if (length > 0 && buffer.WrittenSpan[^1] == (byte)'\r') length--;
                if (length == 0) throw new InvalidOperationException("AGENT_FRAME_EMPTY");
                if (length > MaxFrameBytes) throw new InvalidOperationException("AGENT_FRAME_TOO_LARGE");
                try
                {
                    using var document = JsonDocument.Parse(buffer.WrittenMemory[..length]);
                    var message = document.RootElement.Clone();
                    ValidateMessage(message);
                    return message;
                }
                catch (JsonException error) { throw new InvalidOperationException("INVALID_JSON", error); }
            }
            if (buffer.WrittenCount >= MaxFrameBytes + 1) throw new InvalidOperationException("AGENT_FRAME_TOO_LARGE");
            buffer.Write(oneByte);
        }
    }

    public static void ValidateRequest(JsonElement message)
    {
        ValidateObject(message, "protocol_version", "type", "id");
        RequireVersion(message);
        var type = String(message, "type");
        RequireString(message, "id");
        switch (type)
        {
            case "status": Allowed(message, "protocol_version", "type", "id", "provider"); break;
            case "login": Allowed(message, "protocol_version", "type", "id", "provider", "auth_type", "method", "secret");
                RequireString(message, "provider"); RequireOneOf(message, "auth_type", "oauth", "api_key"); break;
            case "auth_reply": Allowed(message, "protocol_version", "type", "id", "auth_request_id", "prompt_id", "value");
                RequireString(message, "auth_request_id"); RequireString(message, "prompt_id"); RequireString(message, "value"); break;
            case "cancel_auth": Allowed(message, "protocol_version", "type", "id", "auth_request_id"); RequireString(message, "auth_request_id"); break;
            case "cancel_run": Allowed(message, "protocol_version", "type", "id", "run_id", "stage_key", "attempt_id"); RequireRunTarget(message); break;
            case "tool_result": Allowed(message, "protocol_version", "type", "id", "run_id", "stage_key", "attempt_id", "tool_call_id", "result");
                RequireRunTarget(message); RequireString(message, "tool_call_id"); ValidateToolResult(message); break;
            case "run":
                Allowed(message, "protocol_version", "type", "id", "run_id", "stage_key", "attempt_id", "provider", "model", "policy_id", "input_context", "limits");
                RequireRunTarget(message); RequireString(message, "provider"); RequireString(message, "model"); RequireOneOf(message, "policy_id", "research", "analysis", "semantic", "draft");
                ValidateInputContext(message); ValidateLimits(message); break;
            default: throw new InvalidOperationException("UNKNOWN_COMMAND");
        }
    }

    public static void ValidateMessage(JsonElement message)
    {
        ValidateObject(message, "protocol_version", "type");
        RequireVersion(message);
        var type = String(message, "type");
        switch (type)
        {
            case "ready":
                Allowed(message, "protocol_version", "type", "host_version", "pi_version", "supported_policies", "provider_catalog");
                RequireString(message, "host_version"); RequireString(message, "pi_version"); RequireStringArray(message, "supported_policies"); RequireArray(message, "provider_catalog"); break;
            case "response":
                Allowed(message, "protocol_version", "type", "id", "ok", "data", "error", "code");
                RequireString(message, "id"); RequireBoolean(message, "ok"); break;
            case "tool_request":
                Allowed(message, "protocol_version", "type", "run_id", "stage_key", "attempt_id", "sequence", "tool_call_id", "tool", "arguments");
                RequireRunTarget(message); RequirePositiveInt(message, "sequence"); RequireString(message, "tool_call_id"); RequireString(message, "tool");
                if (!message.TryGetProperty("arguments", out _)) throw new InvalidOperationException("MISSING_FIELD:arguments"); break;
            case "run_started": case "progress": case "run_finished":
                Allowed(message, "protocol_version", "type", "run_id", "stage_key", "attempt_id", "sequence", "data", "error"); RequireRunTarget(message); RequirePositiveInt(message, "sequence");
                if (type == "run_finished") { RequireObject(message, "data"); RequireString(message.GetProperty("data"), "state"); }
                break;
            case "auth_url": case "auth_prompt": case "auth_device_code": case "auth_progress": case "auth_prompt_closed": case "auth_finished":
                Allowed(message, "protocol_version", "type", "auth_request_id", "provider", "prompt_id", "url", "code", "kind", "options", "stage", "ok", "error", "data");
                RequireString(message, "auth_request_id"); RequireString(message, "provider");
                if (type is "auth_prompt" or "auth_prompt_closed") RequireString(message, "prompt_id");
                if (type is "auth_url" or "auth_device_code") RequireString(message, "url");
                if (type == "auth_device_code") RequireString(message, "code");
                if (type == "auth_finished") RequireBoolean(message, "ok");
                break;
            case "host_disconnected": Allowed(message, "protocol_version", "type", "code", "message"); RequireString(message, "code"); break;
            case "error": Allowed(message, "protocol_version", "type", "code", "message", "retryable", "details"); RequireString(message, "code"); RequireString(message, "message"); RequireBoolean(message, "retryable"); break;
            default: throw new InvalidOperationException("UNKNOWN_MESSAGE");
        }
    }

    private static void ValidateObject(JsonElement value, params string[] required)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("INVALID_MESSAGE");
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw new InvalidOperationException("DUPLICATE_FIELD");
        foreach (var field in required) if (!value.TryGetProperty(field, out _)) throw new InvalidOperationException("MISSING_FIELD:" + field);
    }

    private static void Allowed(JsonElement value, params string[] allowed)
    {
        var set = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!set.Contains(property.Name)) throw new InvalidOperationException("UNKNOWN_FIELD:" + property.Name);
    }

    private static void RequireVersion(JsonElement message)
    {
        if (!message.GetProperty("protocol_version").TryGetInt32(out var version) || version != Version)
            throw new InvalidOperationException("PROTOCOL_VERSION_UNSUPPORTED");
    }

    private static void RequireRunTarget(JsonElement message)
    {
        RequireString(message, "run_id"); RequireString(message, "stage_key"); RequireString(message, "attempt_id");
    }

    private static void RequireObject(JsonElement message, string field)
    {
        if (!message.GetProperty(field).ValueKind.Equals(JsonValueKind.Object)) throw new InvalidOperationException("INVALID_FIELD:" + field);
    }

    private static void RequireArray(JsonElement message, string field)
    {
        if (!message.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.Array) throw new InvalidOperationException("INVALID_FIELD:" + field);
    }

    private static void RequireStringArray(JsonElement message, string field)
    {
        RequireArray(message, field);
        foreach (var value in message.GetProperty(field).EnumerateArray()) if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw new InvalidOperationException("INVALID_FIELD:" + field);
    }

    private static void RequireBoolean(JsonElement message, string field)
    {
        if (!message.TryGetProperty(field, out var value) || value.ValueKind is not JsonValueKind.True and not JsonValueKind.False) throw new InvalidOperationException("INVALID_FIELD:" + field);
    }

    private static void RequirePositiveInt(JsonElement message, string field)
    {
        if (!message.TryGetProperty(field, out var value) || !value.TryGetInt32(out var number) || number < 1) throw new InvalidOperationException("INVALID_FIELD:" + field);
    }

    private static void ValidateInputContext(JsonElement message)
    {
        RequireObject(message, "input_context");
        var input = message.GetProperty("input_context");
        Allowed(input, "prompt", "output_kind"); RequireString(input, "prompt"); RequireOneOf(input, "output_kind", "research", "analysis", "semantic", "draft");
    }

    private static void ValidateLimits(JsonElement message)
    {
        RequireObject(message, "limits");
        var limits = message.GetProperty("limits");
        Allowed(limits, "max_tool_calls", "timeout_ms"); RequirePositiveInt(limits, "max_tool_calls"); RequirePositiveInt(limits, "timeout_ms");
        var maxToolCalls = limits.GetProperty("max_tool_calls").GetInt32(); var timeout = limits.GetProperty("timeout_ms").GetInt32();
        if (maxToolCalls > 100 || timeout is < 1000 or > 900000) throw new InvalidOperationException("INVALID_FIELD:limits");
    }

    private static void ValidateToolResult(JsonElement message)
    {
        if (!message.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("INVALID_FIELD:result");
        if (!result.TryGetProperty("ok", out var ok) || ok.ValueKind is not JsonValueKind.True and not JsonValueKind.False) throw new InvalidOperationException("INVALID_FIELD:result");
    }

    private static void RequireString(JsonElement message, string field)
    {
        if (!message.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidOperationException("INVALID_FIELD:" + field);
    }

    private static void RequireOneOf(JsonElement message, string field, params string[] choices)
    {
        RequireString(message, field);
        if (!choices.Contains(message.GetProperty(field).GetString(), StringComparer.Ordinal)) throw new InvalidOperationException("INVALID_FIELD:" + field);
    }

    private static string String(JsonElement message, string field) => message.GetProperty(field).GetString() ?? throw new InvalidOperationException("INVALID_FIELD:" + field);
}
