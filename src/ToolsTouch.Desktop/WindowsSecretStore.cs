using System.IO;
using System.Security.Cryptography;
using System.Text;
using ToolsTouch.Core;

namespace ToolsTouch.Desktop;

public sealed class WindowsSecretStore : ISecretStore
{
    private static string PathFor(string name)
    {
        if (name != "gmail") throw new ArgumentException("UNKNOWN_SECRET_NAME");
        var directory = Path.Combine(DesktopSettings.DataDirectory, "credentials"); Directory.CreateDirectory(directory);
        return Path.Combine(directory, name + ".dpapi");
    }
    public string? Read(string name)
    {
        var path = PathFor(name);
        return File.Exists(path) ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser)) : null;
    }
    public void Write(string name, string value)
    {
        var path = PathFor(name);
        File.WriteAllBytes(path + ".tmp", ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
        File.Move(path + ".tmp", path, true);
    }
    public void Delete(string name) => File.Delete(PathFor(name));
}
