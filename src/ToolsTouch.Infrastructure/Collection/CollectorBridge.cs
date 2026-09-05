using System.Diagnostics;
using System.Text;
using System.Text.Json;
using ToolsTouch.Application;

namespace ToolsTouch.Infrastructure.Collection;

public sealed class CollectorBridge(string pythonPath, string storageDirectory, string moduleName = "tools_touch_collector") : ICollectorBridge
{
    private const int MaxStdoutBytes = 64 * 1024;
    private readonly string stagingRoot = Path.Combine(Path.GetFullPath(storageDirectory), "collector-staging");

    public async Task<CollectorResult> ExportBaoyanAsync(CollectorRequest request, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        Directory.CreateDirectory(stagingRoot);
        var id = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(stagingRoot, id);
        var requestDirectory = Path.Combine(Path.GetFullPath(storageDirectory), "collector-requests");
        Directory.CreateDirectory(requestDirectory);
        var requestPath = Path.Combine(requestDirectory, id + ".json");
        Directory.CreateDirectory(staging);
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request), new UTF8Encoding(false), cancellationToken);
        try
        {
            var start = new ProcessStartInfo(pythonPath)
            {
                UseShellExecute = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetFullPath(storageDirectory),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            start.ArgumentList.Add("-m"); start.ArgumentList.Add(moduleName); start.ArgumentList.Add("export-baoyan");
            start.ArgumentList.Add("--request"); start.ArgumentList.Add(requestPath);
            start.ArgumentList.Add("--output"); start.ArgumentList.Add(staging);
            RestrictEnvironment(start);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("COLLECTOR_START_FAILED");
            var stdoutTask = ReadBoundedAsync(process.StandardOutput, MaxStdoutBytes, cancellationToken);
            var stderrTask = ReadBoundedAsync(process.StandardError, MaxStdoutBytes, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.Limits.TimeoutMilliseconds);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                try { await process.WaitForExitAsync(); } catch { }
                throw cancellationToken.IsCancellationRequested ? new InvalidOperationException("COLLECTOR_CANCELLED") : new InvalidOperationException("COLLECTOR_TIMEOUT");
            }
            var stdout = await stdoutTask;
            _ = await stderrTask;
            if (process.ExitCode != 0) throw new InvalidOperationException("COLLECTOR_EXIT_" + process.ExitCode);
            var manifestPath = ParseManifestPath(stdout);
            var expectedManifest = Path.GetFullPath(Path.Combine(staging, "manifest.json"));
            if (!string.Equals(manifestPath, expectedManifest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("COLLECTOR_MANIFEST_PATH_INVALID");
            var manifest = CollectorManifestValidator.ReadAndValidate(staging, request.Limits);
            if (!string.Equals(manifest.Source, request.Source, StringComparison.Ordinal)) throw new InvalidOperationException("COLLECTOR_SOURCE_MISMATCH");
            return new(staging, manifest);
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
        finally { File.Delete(requestPath); }
    }

    private static void ValidateRequest(CollectorRequest request)
    {
        if (request.Source != "baoyan" || string.IsNullOrWhiteSpace(request.Scope.School)) throw new ArgumentException("INVALID_COLLECTOR_REQUEST");
        if (request.Limits.MaxRecords is < 1 or > 100_000 || request.Limits.MaxBytes < 1 || request.Limits.TimeoutMilliseconds is < 1000 or > 3_600_000)
            throw new ArgumentException("INVALID_COLLECTOR_LIMITS");
        if (request.Scope.Kind is not ("全部" or "夏令营" or "预推免")) throw new ArgumentException("INVALID_COLLECTOR_SCOPE");
    }

    private static string ParseManifestPath(string stdout)
    {
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length != 1) throw new InvalidOperationException("COLLECTOR_STDOUT_POLLUTED");
        using var document = JsonDocument.Parse(lines[0]);
        if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().Count() != 1 ||
            !document.RootElement.TryGetProperty("manifest_path", out var path) || path.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(path.GetString()))
            throw new InvalidOperationException("COLLECTOR_STDOUT_INVALID");
        return Path.GetFullPath(path.GetString()!);
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int maxBytes, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            builder.AppendLine(line);
            if (Encoding.UTF8.GetByteCount(builder.ToString()) > maxBytes) throw new InvalidOperationException("COLLECTOR_OUTPUT_TOO_LARGE");
        }
        return builder.ToString();
    }

    private static void RestrictEnvironment(ProcessStartInfo start)
    {
        var keep = new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "HOME", "USERPROFILE", "LOCALAPPDATA", "APPDATA" };
        var values = start.Environment.Where(pair => keep.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)).ToArray();
        start.Environment.Clear();
        foreach (var pair in values) start.Environment[pair.Key] = pair.Value;
        foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY" })
        {
            var value = Environment.GetEnvironmentVariable(key) ?? Environment.GetEnvironmentVariable(key.ToLowerInvariant());
            if (!string.IsNullOrWhiteSpace(value)) start.Environment[key] = value;
        }
        start.Environment["PYTHONNOUSERSITE"] = "1";
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
    }
}
