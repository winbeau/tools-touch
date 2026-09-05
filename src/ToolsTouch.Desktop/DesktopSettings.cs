using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ToolsTouch.Core;

namespace ToolsTouch.Desktop;

public sealed class DesktopSettings
{
    public string NodePath { get; set; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "node", "node.exe")) ? Path.Combine(AppContext.BaseDirectory, "node", "node.exe") : "node";
    public string AgentHostPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "agent-host", "dist", "index.js");
    public string Model { get; set; } = "";
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
        return settings;
    }
    public void Save()
    {
        Directory.CreateDirectory(StorageDirectory);
        File.WriteAllText(SettingsPath + ".tmp", ResearchStore.Serialize(this));
        File.Move(SettingsPath + ".tmp", SettingsPath, true);
    }
}
