using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ToolsTouch.Core;

namespace ToolsTouch.Desktop;

public sealed class DesktopSettings
{
    public string NodePath { get; set; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "node", "node.exe")) ? Path.Combine(AppContext.BaseDirectory, "node", "node.exe") : "node";
    public string AgentHostPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "agent-host", "dist", "index.js");
    public bool AutoOpenLoginBrowser { get; set; } = true;
    public string Model { get; set; } = "";
    public string Provider { get; set; } = "openai-codex";
    public int MaxToolCalls { get; set; } = 30;
    public int StageTimeoutSeconds { get; set; } = 180;
    public string SearchKeyFile { get; set; } = "";
    public string GoogleClientFile { get; set; } = "";
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ToolsTouch");
    [JsonIgnore]
    public string StorageDirectory { get; private set; } = DataDirectory;
    private string SettingsPath => Path.Combine(StorageDirectory, "settings.json");
    public static DesktopSettings Load(string? dataDirectory = null)
    {
        var directory = Path.GetFullPath(dataDirectory ?? DataDirectory);
        var path = Path.Combine(directory, "settings.json");
        var settings = File.Exists(path) ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(path), ResearchStore.Json)
            ?? throw new InvalidDataException("SETTINGS_INVALID") : new();
        settings.StorageDirectory = directory;
        // Saved paths can refer to a deleted portable release. Installed components
        // always take precedence, so upgrading cannot keep a broken old runtime path.
        var bundledHost = Path.Combine(AppContext.BaseDirectory, "agent-host", "dist", "index.js");
        var bundledNode = Path.Combine(AppContext.BaseDirectory, "node", "node.exe");
        if (File.Exists(bundledHost) && File.Exists(bundledNode))
        { settings.AgentHostPath = bundledHost; settings.NodePath = bundledNode; }
        if (!File.Exists(settings.AgentHostPath))
        {
            for (var source = new DirectoryInfo(AppContext.BaseDirectory); source != null; source = source.Parent)
            {
                var candidate = Path.Combine(source.FullName, "agent-host", "dist", "index.js");
                if (File.Exists(candidate)) { settings.AgentHostPath = candidate; break; }
            }
        }
        if (string.IsNullOrWhiteSpace(settings.GoogleClientFile) || !File.Exists(settings.GoogleClientFile))
        {
            var privateClient = Path.Combine(directory, "config", "google-client.json");
            var bundledClient = Path.Combine(AppContext.BaseDirectory, "config", "google-client.json");
            if (File.Exists(privateClient)) settings.GoogleClientFile = privateClient;
            else if (File.Exists(bundledClient)) settings.GoogleClientFile = bundledClient;
        }
        return settings;
    }
    public void Save()
    {
        Directory.CreateDirectory(StorageDirectory);
        File.WriteAllText(SettingsPath + ".tmp", ResearchStore.Serialize(this));
        File.Move(SettingsPath + ".tmp", SettingsPath, true);
    }
}
