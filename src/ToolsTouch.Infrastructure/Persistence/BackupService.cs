using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using ToolsTouch.Application;

namespace ToolsTouch.Core;

public sealed class BackupService(LocalDatabase database) : IWorkspaceBackup
{
    public BackupReport Create(string destinationDirectory)
    {
        var destination = Path.GetFullPath(destinationDirectory);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("BACKUP_DESTINATION_EXISTS");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var staging = destination + ".staging-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        try
        {
            var databaseFile = Path.Combine(staging, "database.db");
            var artifacts = new List<BackupArtifact>();
            WorkspaceMetadata metadata;
            using (var source = database.Open())
            {
                metadata = ReadMetadata(source);
                using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databaseFile, ForeignKeys = true }.ToString());
                target.Open();
                source.BackupDatabase(target);
                CopyReferencedArtifacts(source, staging, artifacts);
            }
            var manifest = new BackupManifest
            {
                BackupVersion = 1,
                SchemaVersion = metadata.SchemaVersion,
                WorkspaceId = metadata.WorkspaceId,
                DataRevision = metadata.DataRevision,
                DatabaseSha256 = HashFile(databaseFile),
                Artifacts = artifacts
            };
            File.WriteAllText(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions));
            Directory.Move(staging, destination);
            return new BackupReport(destination, metadata.WorkspaceId, metadata.DataRevision, artifacts.Count);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            throw;
        }
    }

    public BackupVerification Verify(string backupDirectory)
    {
        var root = Path.GetFullPath(backupDirectory);
        var manifestPath = Path.Combine(root, "manifest.json");
        if (!File.Exists(manifestPath)) throw new InvalidDataException("BACKUP_MANIFEST_MISSING");
        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath), JsonOptions)
            ?? throw new InvalidDataException("BACKUP_MANIFEST_INVALID");
        if (manifest.BackupVersion != 1 || manifest.SchemaVersion < 5 || string.IsNullOrWhiteSpace(manifest.WorkspaceId))
            throw new InvalidDataException("BACKUP_MANIFEST_UNSUPPORTED");
        var databaseFile = Resolve(root, "database.db");
        if (!File.Exists(databaseFile) || !HashFile(databaseFile).Equals(manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("BACKUP_DATABASE_HASH_MISMATCH");
        foreach (var artifact in manifest.Artifacts)
        {
            var path = Resolve(root, artifact.Path);
            if (!File.Exists(path) || new FileInfo(path).Length != artifact.ByteLength || !HashFile(path).Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("BACKUP_ARTIFACT_HASH_MISMATCH: " + artifact.Path);
        }
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databaseFile, Mode = SqliteOpenMode.ReadOnly, ForeignKeys = true }.ToString());
        connection.Open();
        using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check";
            if (!string.Equals(Convert.ToString(integrity.ExecuteScalar()), "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("BACKUP_INTEGRITY_CHECK_FAILED");
        }
        using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_check";
            using var reader = foreignKeys.ExecuteReader();
            if (reader.Read()) throw new InvalidDataException("BACKUP_FOREIGN_KEY_CHECK_FAILED");
        }
        var metadata = ReadMetadata(connection);
        if (metadata.WorkspaceId != manifest.WorkspaceId || metadata.DataRevision != manifest.DataRevision || metadata.SchemaVersion != manifest.SchemaVersion)
            throw new InvalidDataException("BACKUP_METADATA_MISMATCH");
        return new BackupVerification(root, manifest.WorkspaceId, manifest.DataRevision, manifest.Artifacts.Count);
    }

    private void CopyReferencedArtifacts(SqliteConnection source, string staging, List<BackupArtifact> copied)
    {
        using var command = source.CreateCommand();
        command.CommandText = "SELECT RelativePath,ContentHash,ByteLength FROM Artifact ORDER BY RelativePath";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var relative = reader.GetString(0).Replace('\\', '/');
            if (relative.Split('/').Any(part => part is "" or "." or "..") || relative.StartsWith('/'))
                throw new InvalidDataException("ARTIFACT_PATH_INVALID");
            var sourcePath = Resolve(database.ArtifactDirectory, relative);
            var destinationPath = Resolve(Path.Combine(staging, "artifacts"), relative);
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("ARTIFACT_FILE_MISSING", sourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
            var hash = HashFile(destinationPath);
            var length = new FileInfo(destinationPath).Length;
            if (!hash.Equals(reader.GetString(1), StringComparison.OrdinalIgnoreCase) || length != reader.GetInt64(2))
                throw new InvalidDataException("ARTIFACT_SOURCE_HASH_MISMATCH");
            copied.Add(new BackupArtifact("artifacts/" + relative, hash, length));
        }
    }

    private static WorkspaceMetadata ReadMetadata(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT WorkspaceId,DataRevision,SchemaVersion FROM WorkspaceMeta WHERE Id=1";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("WORKSPACE_META_MISSING");
        return new WorkspaceMetadata(reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2));
    }

    private static string Resolve(string root, string relative)
    {
        relative = relative.Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException("BACKUP_PATH_ESCAPE");
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("BACKUP_PATH_ESCAPE");
        return full;
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private sealed class BackupManifest
    {
        public int BackupVersion { get; set; }
        public int SchemaVersion { get; set; }
        public string WorkspaceId { get; set; } = "";
        public long DataRevision { get; set; }
        public string DatabaseSha256 { get; set; } = "";
        public List<BackupArtifact> Artifacts { get; set; } = [];
    }

    private sealed record BackupArtifact(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("sha256")] string Sha256,
        [property: JsonPropertyName("byte_length")] long ByteLength);
}
