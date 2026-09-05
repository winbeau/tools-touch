using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using ToolsTouch.Application;
using ToolsTouch.Core;

namespace ToolsTouch.Infrastructure.Persistence;

public sealed class WorkspaceExportService(LocalDatabase database, string pythonPath = "python") : IWorkspaceExportService
{
    private static readonly HashSet<string> HistoryTables = new(StringComparer.Ordinal)
    {
        "AgentRunEvent", "ApplicationEvent", "ClaimSupport", "DraftChange", "RecordChange", "SourceCheck"
    };

    public WorkspaceExportResult Export(WorkspaceExportRequest request, CancellationToken cancellationToken = default)
    {
        var scope = NormalizeScope(request.Scope);
        var format = NormalizeFormat(request.Format);
        var output = Path.GetFullPath(Required(request.OutputPath, nameof(request.OutputPath)));
        if (File.Exists(output) || Directory.Exists(output)) throw new IOException("EXPORT_DESTINATION_EXISTS");
        if (format == "business_bundle" && scope != "WorkspaceAll") throw new InvalidOperationException("BUSINESS_BUNDLE_REQUIRES_WORKSPACE_SCOPE");
        if (format == "business_bundle" && !request.IncludeHistory) throw new InvalidOperationException("FULL_BACKUP_REQUIRES_HISTORY");
        var staging = output + ".staging-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(staging);
        var snapshotPath = Path.Combine(Path.GetTempPath(), "tools-touch-export-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var metadata = CreateSnapshot(snapshotPath);
            using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = snapshotPath, ForeignKeys = true, Pooling = false }.ToString());
            snapshot.Open();
            var tableDumps = DumpTables(snapshot, staging, scope, request.CollectionId, request.IncludeHistory, cancellationToken);
            CopyAttachments(snapshot, staging, request.IncludeAttachments, cancellationToken, out var excluded, out var missingFiles);
            var rowCount = tableDumps.Sum(item => item.RowCount);
            if (format == "business_bundle")
            {
                var manifest = new BundleManifest
                {
                    ExportVersion = 1,
                    SchemaVersion = metadata.SchemaVersion,
                    WorkspaceId = metadata.WorkspaceId,
                    DataRevision = metadata.DataRevision,
                    CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
                    FullBackup = true,
                    Tables = tableDumps.Select(item => new BundleTable(item.Name, "tables/" + item.Name + ".jsonl", item.RowCount)).ToList(),
                    Files = tableDumps.Select(item => item.File).Concat(ReadCopiedFiles(staging, excluded)).ToList(),
                    Excluded = excluded,
                    MissingFiles = missingFiles
                };
                WriteManifest(staging, manifest);
                cancellationToken.ThrowIfCancellationRequested();
                PublishZip(staging, output);
            }
            else
            {
                var requestPath = Path.Combine(staging, "xlsx-request.json");
                var xlsxRequest = new XlsxRequest
                {
                    SchemaVersion = 1,
                    MaxRowsPerSheet = request.MaxRowsPerSheet,
                    Tables = tableDumps.Select(item => new XlsxTable(item.Name, "tables/" + item.Name + ".jsonl", item.Columns, item.RowCount)).ToList()
                };
                File.WriteAllText(requestPath, JsonSerializer.Serialize(xlsxRequest, JsonOptions));
                RunXlsxExporter(requestPath, staging, cancellationToken);
                var workbook = Path.Combine(staging, "workbook.xlsx");
                if (!File.Exists(workbook)) throw new InvalidDataException("XLSX_OUTPUT_MISSING");
                PublishFile(workbook, output);
            }
            return new(output, format, metadata.WorkspaceId, metadata.DataRevision, tableDumps.Count, rowCount);
        }
        catch
        {
            if (File.Exists(output)) File.Delete(output);
            throw;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            if (File.Exists(snapshotPath)) File.Delete(snapshotPath);
        }
    }

    public WorkspaceRestoreResult RestoreToNewWorkspace(string bundlePath, string destinationDirectory, CancellationToken cancellationToken = default)
    {
        var bundle = Path.GetFullPath(Required(bundlePath, nameof(bundlePath)));
        var destination = Path.GetFullPath(Required(destinationDirectory, nameof(destinationDirectory)));
        if (!File.Exists(bundle)) throw new FileNotFoundException("BUNDLE_NOT_FOUND", bundle);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("RESTORE_DESTINATION_EXISTS");
        var extraction = Path.Combine(Path.GetTempPath(), "tools-touch-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extraction);
        try
        {
            ExtractSafe(bundle, extraction);
            var manifest = ReadManifest(extraction);
            VerifyBundleFiles(extraction, manifest, cancellationToken);
            Directory.CreateDirectory(destination);
            var databasePath = Path.Combine(destination, "tools-touch.db");
            var restored = new LocalDatabase(databasePath) { Pooling = false };
            restored.Initialize();
            var rows = RestoreTables(restored, extraction, manifest, cancellationToken);
            RestoreAttachments(extraction, restored.ArtifactDirectory, manifest, cancellationToken);
            return new(destination, databasePath, manifest.WorkspaceId, manifest.DataRevision, rows);
        }
        catch
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            throw;
        }
        finally
        {
            if (Directory.Exists(extraction)) Directory.Delete(extraction, true);
        }
    }

    private WorkspaceMetadata CreateSnapshot(string path)
    {
        using var source = database.Open();
        using var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = false }.ToString());
        target.Open();
        source.BackupDatabase(target);
        return ReadMetadata(target);
    }

    private static List<TableDump> DumpTables(SqliteConnection snapshot, string staging, string scope, string? collectionId,
        bool includeHistory, CancellationToken cancellationToken)
    {
        var tables = ListTables(snapshot).Where(table => includeHistory || !HistoryTables.Contains(table)).ToArray();
        if (scope == "CollectionAll")
        {
            if (string.IsNullOrWhiteSpace(collectionId)) throw new ArgumentException("COLLECTION_ID_REQUIRED", nameof(collectionId));
            tables = tables.Where(table => CollectionTableNames.Contains(table) || table == "WorkspaceMeta" || table == "SchemaVersion").ToArray();
        }
        var directory = Path.Combine(staging, "tables");
        Directory.CreateDirectory(directory);
        var result = new List<TableDump>();
        foreach (var table in tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var where = CollectionWhere(table, scope, collectionId);
            var dump = DumpTable(snapshot, directory, table, where, collectionId, cancellationToken);
            result.Add(dump);
        }
        return result;
    }

    private static TableDump DumpTable(SqliteConnection connection, string directory, string table, string? where, string? collectionId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM " + QuoteIdentifier(table) + (where is null ? "" : " WHERE " + where);
        if (collectionId is not null && where is not null) command.Parameters.AddWithValue("$collection", collectionId);
        using var reader = command.ExecuteReader();
        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var path = Path.Combine(directory, table + ".jsonl");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            var rowCount = 0L;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (var json = new Utf8JsonWriter(stream))
                {
                    json.WriteStartObject();
                    for (var index = 0; index < columns.Length; index++) WriteValue(json, columns[index], reader.IsDBNull(index) ? null : reader.GetValue(index));
                    json.WriteEndObject();
                    json.Flush();
                }
                stream.WriteByte((byte)'\n');
                rowCount++;
            }
            stream.Flush(true);
            if (rowCount < 0) throw new InvalidDataException("EXPORT_ROW_COUNT_INVALID");
        }
        return new TableDump(table, columns, CountRows(path), FileEntry("tables/" + table + ".jsonl", path, "table"));
    }

    private static long CountRows(string path) => File.ReadLines(path).LongCount();

    private void CopyAttachments(SqliteConnection snapshot, string staging, bool includeAttachments, CancellationToken cancellationToken,
        out List<string> excluded, out List<string> missingFiles)
    {
        excluded = [];
        missingFiles = [];
        using var command = snapshot.CreateCommand();
        command.CommandText = "SELECT RelativePath,ContentHash,ByteLength FROM Artifact ORDER BY RelativePath";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = SafeRelative(reader.GetString(0));
            var bundlePath = "artifacts/" + relative;
            var source = Resolve(database.ArtifactDirectory, relative);
            if (!includeAttachments)
            {
                excluded.Add(bundlePath);
                missingFiles.Add(bundlePath);
                continue;
            }
            if (!File.Exists(source)) throw new FileNotFoundException("ARTIFACT_FILE_MISSING", source);
            var destination = Resolve(Path.Combine(staging, "artifacts"), relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
            var length = new FileInfo(destination).Length;
            var hash = HashFile(destination);
            if (!hash.Equals(reader.GetString(1), StringComparison.OrdinalIgnoreCase) || length != reader.GetInt64(2))
                throw new InvalidDataException("ARTIFACT_SOURCE_HASH_MISMATCH");
        }
    }

    private static IReadOnlyList<BundleFile> ReadCopiedFiles(string staging, IReadOnlyList<string> excluded)
    {
        var excludedSet = excluded.ToHashSet(StringComparer.Ordinal);
        return Directory.Exists(Path.Combine(staging, "artifacts"))
            ? Directory.GetFiles(Path.Combine(staging, "artifacts"), "*", SearchOption.AllDirectories).Select(path =>
            {
                var relative = "artifacts/" + Path.GetRelativePath(Path.Combine(staging, "artifacts"), path).Replace(Path.DirectorySeparatorChar, '/');
                return FileEntry(relative, path, "attachment");
            }).Where(file => !excludedSet.Contains(file.Path)).ToArray()
            : [];
    }

    private void RunXlsxExporter(string requestPath, string outputDirectory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo { FileName = pythonPath, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(pythonPath).Equals("uv", StringComparison.OrdinalIgnoreCase))
        {
            start.ArgumentList.Add("run"); start.ArgumentList.Add("--all-packages"); start.ArgumentList.Add("--locked"); start.ArgumentList.Add("python");
        }
        else start.ArgumentList.Add("-m");
        if (!Path.GetFileNameWithoutExtension(pythonPath).Equals("uv", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add("tools_touch_collector");
        else { start.ArgumentList.Add("-m"); start.ArgumentList.Add("tools_touch_collector"); }
        start.ArgumentList.Add("export-xlsx"); start.ArgumentList.Add("--request"); start.ArgumentList.Add(requestPath); start.ArgumentList.Add("--output"); start.ArgumentList.Add(outputDirectory);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("XLSX_PROCESS_START_FAILED");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        process.WaitForExit();
        var output = stdout.GetAwaiter().GetResult();
        var error = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new InvalidOperationException("XLSX_EXPORT_FAILED: " + error.Trim());
        if (string.IsNullOrWhiteSpace(output)) throw new InvalidDataException("XLSX_RESULT_MISSING");
    }

    private static void WriteManifest(string staging, BundleManifest manifest) =>
        File.WriteAllText(Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(manifest, JsonOptions) + "\n");

    private static void PublishZip(string staging, string output)
    {
        var temporary = output + ".tmp-" + Guid.NewGuid().ToString("N");
        try { ZipFile.CreateFromDirectory(staging, temporary, CompressionLevel.Optimal, false); File.Move(temporary, output); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void PublishFile(string source, string output)
    {
        var temporary = output + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.Copy(source, temporary); File.Move(temporary, output); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ExtractSafe(string bundle, string destination)
    {
        using var archive = ZipFile.OpenRead(bundle);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            var rawName = entry.FullName.Replace('\\', '/');
            var isDirectory = rawName.EndsWith("/", StringComparison.Ordinal);
            var relative = SafeRelative(isDirectory ? rawName.TrimEnd('/') : rawName);
            if (relative.Length == 0) continue;
            if (!names.Add(relative)) throw new InvalidDataException("BUNDLE_DUPLICATE_PATH");
            var path = Resolve(destination, relative);
            if (isDirectory) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var source = entry.Open();
            using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
        }
    }

    private static BundleManifest ReadManifest(string extraction)
    {
        var path = Resolve(extraction, "manifest.json");
        if (!File.Exists(path)) throw new InvalidDataException("BUNDLE_MANIFEST_MISSING");
        var manifest = JsonSerializer.Deserialize<BundleManifest>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("BUNDLE_MANIFEST_INVALID");
        if (manifest.ExportVersion != 1 || manifest.SchemaVersion is not (15 or 16) || string.IsNullOrWhiteSpace(manifest.WorkspaceId) || !manifest.FullBackup)
            throw new InvalidDataException("BUNDLE_MANIFEST_UNSUPPORTED");
        return manifest;
    }

    private static void VerifyBundleFiles(string extraction, BundleManifest manifest, CancellationToken cancellationToken)
    {
        var listed = manifest.Files.ToDictionary(file => file.Path, StringComparer.Ordinal);
        foreach (var table in manifest.Tables)
        {
            if (!listed.ContainsKey(table.Path)) throw new InvalidDataException("BUNDLE_TABLE_FILE_MISSING");
        }
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Resolve(extraction, file.Path);
            if (!File.Exists(path) || new FileInfo(path).Length != file.ByteLength || !HashFile(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("BUNDLE_FILE_HASH_MISMATCH: " + file.Path);
        }
    }

    private static long RestoreTables(LocalDatabase database, string extraction, BundleManifest manifest, CancellationToken cancellationToken)
    {
        var rowCount = 0L;
        using var connection = database.Open();
        using (var foreignKeys = connection.CreateCommand()) { foreignKeys.CommandText = "PRAGMA foreign_keys=OFF"; foreignKeys.ExecuteNonQuery(); }
        using var transaction = connection.BeginTransaction();
        foreach (var table in manifest.Tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Resolve(extraction, table.Path);
            using var stream = new StreamReader(path);
            string? line;
            var tableRows = 0L;
            while ((line = stream.ReadLine()) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("BUNDLE_ROW_INVALID");
                InsertRow(connection, transaction, table.Name, document.RootElement);
                tableRows++;
                rowCount++;
            }
            if (tableRows != table.RowCount) throw new InvalidDataException("BUNDLE_ROW_COUNT_MISMATCH: " + table.Name);
        }
        NormalizeRestoredPendingSends(connection, transaction);
        transaction.Commit();
        using (var foreignKeys = connection.CreateCommand()) { foreignKeys.CommandText = "PRAGMA foreign_keys=ON"; foreignKeys.ExecuteNonQuery(); }
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA foreign_key_check";
            using var reader = check.ExecuteReader();
            if (reader.Read()) throw new InvalidDataException("BUNDLE_FOREIGN_KEY_CHECK_FAILED");
        }
        return rowCount;
    }

    private static void RestoreAttachments(string extraction, string artifactDirectory, BundleManifest manifest, CancellationToken cancellationToken)
    {
        foreach (var file in manifest.Files.Where(file => file.Kind == "attachment"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = file.Path.StartsWith("artifacts/", StringComparison.Ordinal) ? file.Path["artifacts/".Length..] : throw new InvalidDataException("BUNDLE_ATTACHMENT_PATH_INVALID");
            var source = Resolve(extraction, file.Path);
            var destination = Resolve(artifactDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
    }

    private static void InsertRow(SqliteConnection connection, SqliteTransaction transaction, string table, JsonElement row)
    {
        var columns = TableColumns(connection, transaction, table);
        var properties = row.EnumerateObject().ToArray();
        if (properties.Length == 0 || properties.Any(property => !columns.Contains(property.Name))) throw new InvalidDataException("BUNDLE_COLUMN_INVALID: " + table);
        var names = properties.Select(property => QuoteIdentifier(property.Name));
        var parameters = properties.Select((_, index) => "$value" + index.ToString(CultureInfo.InvariantCulture)).ToArray();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO " + QuoteIdentifier(table) + " (" + string.Join(',', names) + ") VALUES (" + string.Join(',', parameters) + ")";
        for (var index = 0; index < properties.Length; index++) command.Parameters.AddWithValue(parameters[index], SqlValue(properties[index].Value));
        command.ExecuteNonQuery();
    }

    private static HashSet<string> TableColumns(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "PRAGMA table_info(" + QuoteIdentifier(table) + ")";
        using var reader = command.ExecuteReader();
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) result.Add(reader.GetString(1));
        if (result.Count == 0) throw new InvalidDataException("BUNDLE_TABLE_NOT_ALLOWED: " + table);
        return result;
    }

    private static object SqlValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => DBNull.Value,
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => 1,
        JsonValueKind.False => 0,
        _ => value.GetRawText()
    };

    private static void NormalizeRestoredPendingSends(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var outreach = LocalDatabase.Command(connection, "UPDATE Outreach SET State='Unknown',Error=COALESCE(Error,'RESTORED_REQUIRES_RECONCILIATION') WHERE State='Sending'");
        outreach.Transaction = transaction; outreach.ExecuteNonQuery();
        using var attempts = LocalDatabase.Command(connection, "UPDATE SendAttempt SET State='Unknown',Error=COALESCE(Error,'RESTORED_REQUIRES_RECONCILIATION') WHERE State IN ('Sending','Transmitting')");
        attempts.Transaction = transaction; attempts.ExecuteNonQuery();
    }

    private static WorkspaceMetadata ReadMetadata(SqliteConnection connection)
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT WorkspaceId,DataRevision,SchemaVersion FROM WorkspaceMeta WHERE Id=1";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("WORKSPACE_META_MISSING");
        return new WorkspaceMetadata(reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2));
    }

    private static IReadOnlyList<string> ListTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand(); command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        using var reader = command.ExecuteReader(); var result = new List<string>(); while (reader.Read()) result.Add(reader.GetString(0)); return result;
    }

    private static string? CollectionWhere(string table, string scope, string? collectionId) => scope == "CollectionAll" && CollectionTableNames.Contains(table) ? table switch
    {
        "Collection" => "Id=$collection",
        "RecordRef" => "CollectionId=$collection",
        "FieldDefinition" or "ViewDefinition" => "CollectionId=$collection",
        "FieldChoice" => "FieldId IN (SELECT Id FROM FieldDefinition WHERE CollectionId=$collection)",
        "FieldValue" => "RecordId IN (SELECT Id FROM RecordRef WHERE CollectionId=$collection)",
        "RecordRelation" => "FromRecordId IN (SELECT Id FROM RecordRef WHERE CollectionId=$collection)",
        "RecordChange" => "RecordId IN (SELECT Id FROM RecordRef WHERE CollectionId=$collection)",
        _ => null
    } : null;

    private static readonly HashSet<string> CollectionTableNames = new(StringComparer.Ordinal)
    {
        "Collection", "RecordRef", "FieldDefinition", "FieldChoice", "FieldValue", "RecordRelation", "ViewDefinition", "RecordChange"
    };

    private static BundleFile FileEntry(string path, string source, string kind) => new(path, HashFile(source), new FileInfo(source).Length, kind);

    private static void WriteValue(Utf8JsonWriter writer, string name, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNull(name); break;
            case string text: writer.WriteString(name, text); break;
            case byte[] bytes: writer.WriteBase64String(name, bytes); break;
            case long integer: writer.WriteNumber(name, integer); break;
            case int integer: writer.WriteNumber(name, integer); break;
            case short integer: writer.WriteNumber(name, integer); break;
            case double number: writer.WriteNumber(name, number); break;
            case float number: writer.WriteNumber(name, number); break;
            case decimal number: writer.WriteNumber(name, number); break;
            default: writer.WriteString(name, Convert.ToString(value, CultureInfo.InvariantCulture)); break;
        }
    }

    private static string SafeRelative(string relative)
    {
        relative = relative.Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative.Split('/').Any(part => part is "" or "." or "..")) throw new InvalidDataException("BUNDLE_PATH_INVALID");
        return relative;
    }

    private static string Resolve(string root, string relative)
    {
        relative = SafeRelative(relative);
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("BUNDLE_PATH_ESCAPE");
        return full;
    }

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Required(string value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A value is required.", name) : value.Trim();
    private static string NormalizeScope(string scope) => scope.Trim() switch { "WorkspaceAll" => "WorkspaceAll", "CollectionAll" => "CollectionAll", _ => throw new InvalidOperationException("EXPORT_SCOPE_UNSUPPORTED") };
    private static string NormalizeFormat(string format) => format.Trim().ToLowerInvariant() switch { "business_bundle" or "business-bundle" => "business_bundle", "xlsx" => "xlsx", _ => throw new InvalidOperationException("EXPORT_FORMAT_UNSUPPORTED") };

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private sealed record TableDump(string Name, IReadOnlyList<string> Columns, long RowCount, BundleFile File);
    private sealed record BundleFile(string Path, string Sha256, long ByteLength, string Kind);
    private sealed record BundleTable(string Name, string Path, long RowCount);
    private sealed class BundleManifest
    {
        public int ExportVersion { get; set; }
        public int SchemaVersion { get; set; }
        public string WorkspaceId { get; set; } = "";
        public long DataRevision { get; set; }
        public string CreatedAt { get; set; } = "";
        public bool FullBackup { get; set; }
        public List<BundleTable> Tables { get; set; } = [];
        public List<BundleFile> Files { get; set; } = [];
        public List<string> Excluded { get; set; } = [];
        public List<string> MissingFiles { get; set; } = [];
    }
    private sealed class XlsxRequest
    {
        public int SchemaVersion { get; set; }
        public int MaxRowsPerSheet { get; set; }
        public List<XlsxTable> Tables { get; set; } = [];
    }
    private sealed record XlsxTable(string Name, string Path, IReadOnlyList<string> Columns, long RowCount);
}
