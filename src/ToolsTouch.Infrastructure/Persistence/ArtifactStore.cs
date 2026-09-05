using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace ToolsTouch.Core;

public sealed class ArtifactStore(string rootDirectory)
{
    public string RootDirectory { get; } = Path.GetFullPath(rootDirectory);

    public Artifact Stage(string kind, ReadOnlyMemory<byte> content, string mimeType, string extension = ".bin")
    {
        kind = SafeSegment(kind, nameof(kind));
        extension = extension.StartsWith('.') ? extension : "." + extension;
        if (extension.Any(character => character is '/' or '\\' or ':' or '\0'))
            throw new ArgumentException("Invalid artifact extension.", nameof(extension));
        var hash = Convert.ToHexString(SHA256.HashData(content.Span)).ToLowerInvariant();
        var relativePath = $"{kind}/{hash}{extension}";
        var artifact = new Artifact(hash, kind, relativePath, hash, content.Length, mimeType, DateTimeOffset.UtcNow);
        var destination = Path.Combine(RootDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(destination is null ? RootDirectory : Path.GetDirectoryName(destination)!);
        if (!File.Exists(destination))
        {
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, content.ToArray());
                File.Move(temporary, destination!);
            }
            catch (IOException) when (File.Exists(destination))
            {
                File.Delete(temporary);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
        if (new FileInfo(destination!).Length != content.Length ||
            !Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(destination!))).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("ARTIFACT_CONTENT_MISMATCH");
        return artifact;
    }

    public static void Insert(SqliteConnection connection, SqliteTransaction transaction, Artifact artifact)
    {
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO Artifact(Id,Kind,RelativePath,ContentHash,ByteLength,MimeType,CreatedAt)
            VALUES($id,$kind,$path,$hash,$length,$mime,$created)
            ON CONFLICT(ContentHash) DO NOTHING
            """, ("$id", artifact.Id), ("$kind", artifact.Kind), ("$path", artifact.RelativePath),
            ("$hash", artifact.ContentHash), ("$length", artifact.ByteLength), ("$mime", artifact.MimeType),
            ("$created", artifact.CreatedAt.ToString("O")));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    public static string ReadText(Artifact artifact, string rootDirectory)
    {
        var root = Path.GetFullPath(rootDirectory);
        var path = Path.GetFullPath(Path.Combine(root, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && path != root)
            throw new InvalidOperationException("ARTIFACT_PATH_ESCAPE");
        return File.ReadAllText(path, Encoding.UTF8);
    }

    public byte[] ReadBytes(Artifact artifact)
    {
        var path = Resolve(artifact.RelativePath);
        if (!File.Exists(path)) throw new FileNotFoundException("ARTIFACT_FILE_MISSING", path);
        var bytes = File.ReadAllBytes(path);
        if (bytes.LongLength != artifact.ByteLength ||
            !Convert.ToHexString(SHA256.HashData(bytes)).Equals(artifact.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("ARTIFACT_CONTENT_MISMATCH");
        return bytes;
    }

    private string Resolve(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(RootDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(RootDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relativePath.Replace('\\', '/').Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidOperationException("ARTIFACT_PATH_ESCAPE");
        return path;
    }

    private static string SafeSegment(string value, string name)
    {
        value = School.Required(value, name);
        if (value is "." or ".." || value.Any(character => character is '/' or '\\' or ':' or '\0'))
            throw new ArgumentException("Artifact kind must be one relative path segment.", name);
        return value;
    }
}
