using System.IO;
using System.Text.Json;
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
    private static string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public static DesktopSettings Load() => File.Exists(SettingsPath) ? JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(SettingsPath), ResearchStore.Json)! : new();
    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(SettingsPath + ".tmp", ResearchStore.Serialize(this));
        File.Move(SettingsPath + ".tmp", SettingsPath, true);
    }
}
