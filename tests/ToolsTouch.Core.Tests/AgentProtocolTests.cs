using System.Text;
using System.Text.Json;
using ToolsTouch.Application;

static class AgentProtocolTests
{
    public static async Task RunAsync()
    {
        var prepared = AgentProtocol.PrepareRequest(new { type = "status", id = "protocol-test" });
        Check(prepared.GetProperty("protocol_version").GetInt32() == AgentProtocol.Version, "requests receive protocol version 2");
        var frame = AgentProtocol.Encode(prepared);
        Check(frame[^1] == (byte)'\n' && Encoding.UTF8.GetByteCount(Encoding.UTF8.GetString(frame[..^1])) <= AgentProtocol.MaxFrameBytes,
            "encoded requests use a bounded UTF-8 JSONL frame");

        var readyFrame = AgentProtocol.Encode(new
        {
            protocol_version = AgentProtocol.Version, type = "ready", host_version = "test", pi_version = "test",
            supported_policies = new[] { "research", "analysis", "semantic", "draft" }, provider_catalog = Array.Empty<object>()
        });
        await using (var stream = new MemoryStream(readyFrame))
        {
            var decoded = await AgentProtocol.ReadFrameAsync(stream);
            Check(decoded?.GetProperty("type").GetString() == "ready", "framed event can be decoded");
        }

        var run = AgentProtocol.PrepareRequest(new
        {
            type = "cancel_run", id = "cancel-request", run_id = "run-1", stage_key = "Research", attempt_id = "attempt-1"
        });
        AgentProtocol.ValidateRequest(run);
        Check(run.GetProperty("attempt_id").GetString() == "attempt-1", "cancellation carries the exact run target");

        Throws(() => AgentProtocol.PrepareRequest(new { protocol_version = 1, type = "status", id = "old" }), "old protocol versions are rejected");
        Throws(() => AgentProtocol.PrepareRequest(new { type = "status", id = "unknown", unexpected = true }), "unknown request fields are rejected");
        Throws(() => AgentProtocol.ValidateMessage(JsonSerializer.SerializeToElement(new { protocol_version = 2, type = "progress", run_id = "r", stage_key = "s", attempt_id = "a", unexpected = true })),
            "unknown event fields are rejected");

        var oversized = JsonSerializer.SerializeToElement(new { protocol_version = 2, type = "status", id = "large", provider = new string('你', 200_000) });
        Throws(() => AgentProtocol.Encode(oversized), "oversized UTF-8 frames are rejected by byte length");
        Console.WriteLine("PASS: protocol v2 strict fields, UTF-8 byte limits, version rejection and exact run targets");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception(message);
    }
}
