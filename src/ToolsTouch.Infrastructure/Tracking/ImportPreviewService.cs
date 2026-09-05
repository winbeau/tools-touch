using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ToolsTouch.Application;
using ToolsTouch.Core;

namespace ToolsTouch.Infrastructure.Tracking;

public sealed class ImportPreviewService(LocalDatabase database, string pythonPath = "python") : IImportPreviewService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SystemCollectionAdapter catalog = new(database);
    private readonly RecordWorkspaceService records = new(database);

    public ImportPreviewResult Preview(ImportRequest request, CancellationToken cancellationToken = default)
    {
        var validated = ValidateRequest(request);
        var sourceHash = HashFile(validated.SourcePath);
        var mappingJson = JsonSerializer.Serialize(validated.Mappings, JsonOptions);
        var existing = FindBatchByCommand(validated.CommandId);
        if (existing is not null)
        {
            if (!string.Equals(existing.SourceHash, sourceHash, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existing.CollectionId, validated.CollectionId, StringComparison.Ordinal) ||
                !string.Equals(existing.Format, validated.Format, StringComparison.Ordinal) ||
                !string.Equals(existing.DuplicateStrategy, validated.DuplicateStrategy, StringComparison.Ordinal) ||
                !string.Equals(existing.SheetName, validated.SheetName, StringComparison.Ordinal) ||
                !string.Equals(existing.MappingJson, mappingJson, StringComparison.Ordinal))
                throw new InvalidOperationException("IMPORT_IDEMPOTENCY_CONFLICT");
        }

        var batchId = existing?.Id ?? "import-" + Guid.NewGuid().ToString("N");
        var prepared = Prepare(validated, batchId, sourceHash, cancellationToken);
        if (existing is null)
        {
            using var connection = database.Open();
            using var transaction = connection.BeginTransaction();
            using var insert = LocalDatabase.Command(connection, """
                INSERT INTO ImportBatch(Id,SourceKind,State,CommandId,ScannedCount,AcceptedCount,RejectedCount,CreatedAt,
                    CollectionId,SourcePath,SourceHash,Format,DuplicateStrategy,SheetName,MappingJson,ErrorsJson)
                VALUES($id,$source,'Preview',$command,$scanned,$accepted,$rejected,$created,
                    $collection,$path,$hash,$format,$strategy,$sheet,$mapping,$errors)
                """, ("$id", batchId), ("$source", validated.Format), ("$command", validated.CommandId),
                ("$scanned", (object?)prepared.Rows.Count), ("$accepted", (object?)prepared.AcceptedCount), ("$rejected", (object?)(prepared.Rows.Count - prepared.AcceptedCount)),
                ("$created", Now), ("$collection", validated.CollectionId), ("$path", validated.SourcePath), ("$hash", sourceHash),
                ("$format", validated.Format), ("$strategy", validated.DuplicateStrategy), ("$sheet", validated.SheetName), ("$mapping", mappingJson),
                ("$errors", JsonSerializer.Serialize(prepared.Errors, JsonOptions)));
            insert.Transaction = transaction;
            insert.ExecuteNonQuery();
            transaction.Commit();
        }
        return ToPreview(batchId, validated, sourceHash, prepared);
    }

    public ImportCommitResult Commit(string batchId, CancellationToken cancellationToken = default)
    {
        batchId = Required(batchId, nameof(batchId));
        var batch = FindBatch(batchId) ?? throw new KeyNotFoundException("IMPORT_BATCH_NOT_FOUND");
        if (batch.State == "Committed") return ToCommit(batch, []);
        if (batch.State != "Preview") return ToCommit(batch, ReadErrors(batch.ErrorsJson));
        if (string.Equals(batch.DuplicateStrategy, "PreviewOnly", StringComparison.Ordinal))
        {
            var error = new ImportRowError(0, "PREVIEW_ONLY", null, null, "该批次只允许预览，不能提交。");
            MarkFailed(batch.Id, "PREVIEW_ONLY", [error]);
            return ToCommit(batch with { State = "Failed", Error = "PREVIEW_ONLY", ErrorsJson = JsonSerializer.Serialize(new[] { error }, JsonOptions) }, [error]);
        }

        var sourcePath = Required(batch.SourcePath, "SourcePath");
        if (!File.Exists(sourcePath))
        {
            var error = new ImportRowError(0, "SOURCE_MISSING", null, null, "导入源文件不存在或已被移除。");
            MarkFailed(batch.Id, "SOURCE_MISSING", [error]);
            return ToCommit(batch with { State = "Failed", Error = "SOURCE_MISSING", ErrorsJson = JsonSerializer.Serialize(new[] { error }, JsonOptions) }, [error]);
        }
        var currentHash = HashFile(sourcePath);
        if (!string.Equals(currentHash, batch.SourceHash, StringComparison.OrdinalIgnoreCase))
        {
            var error = new ImportRowError(0, "SOURCE_CHANGED", null, null, "预览后导入文件已变化，请重新预览。");
            MarkFailed(batch.Id, "SOURCE_CHANGED", [error]);
            return ToCommit(batch with { State = "Failed", Error = "SOURCE_CHANGED", ErrorsJson = JsonSerializer.Serialize(new[] { error }, JsonOptions) }, [error]);
        }

        var request = new ValidatedRequest(batch.CollectionId!, sourcePath, batch.CommandId,
            JsonSerializer.Deserialize<IReadOnlyList<ImportFieldMapping>>(batch.MappingJson!, JsonOptions) ?? [],
            batch.Format!, batch.DuplicateStrategy!, batch.SheetName, 100_000);
        var prepared = Prepare(request, batch.Id, batch.SourceHash!, cancellationToken);
        if (prepared.Errors.Count != 0)
        {
            MarkFailed(batch.Id, "IMPORT_VALIDATION_FAILED", prepared.Errors);
            return ToCommit(batch with
            {
                State = "Failed", Error = "IMPORT_VALIDATION_FAILED",
                ScannedCount = prepared.Rows.Count, AcceptedCount = prepared.AcceptedCount,
                RejectedCount = prepared.Rows.Count - prepared.AcceptedCount, ErrorsJson = JsonSerializer.Serialize(prepared.Errors, JsonOptions)
            }, prepared.Errors);
        }

        try
        {
            var result = records.ApplyImportedRows(batch.CollectionId!, prepared.WriteRows);
            MarkCommitted(batch.Id, prepared.Rows.Count, prepared.AcceptedCount, prepared.Rows.Count - prepared.AcceptedCount);
            return new ImportCommitResult(batch.Id, "Committed", prepared.Rows.Count, prepared.AcceptedCount, prepared.Rows.Count - prepared.AcceptedCount,
                result.CreatedCount, result.UpdatedCount, []);
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            var rowError = new ImportRowError(0, ErrorCode(error.Message), null, null, error.Message);
            MarkFailed(batch.Id, "IMPORT_COMMIT_FAILED", [rowError]);
            return new ImportCommitResult(batch.Id, "Failed", prepared.Rows.Count, 0, prepared.Rows.Count, 0, 0, [rowError]);
        }
    }

    public ImportCommitResult Cancel(string batchId)
    {
        batchId = Required(batchId, nameof(batchId));
        var batch = FindBatch(batchId) ?? throw new KeyNotFoundException("IMPORT_BATCH_NOT_FOUND");
        if (batch.State is "Committed" or "Failed" or "Cancelled") return ToCommit(batch, ReadErrors(batch.ErrorsJson));
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var update = LocalDatabase.Command(connection, "UPDATE ImportBatch SET State='Cancelled',FinishedAt=$finished,Error='IMPORT_CANCELLED' WHERE Id=$id AND State='Preview'",
            ("$id", batch.Id), ("$finished", Now));
        update.Transaction = transaction;
        update.ExecuteNonQuery();
        transaction.Commit();
        return new ImportCommitResult(batch.Id, "Cancelled", batch.ScannedCount, batch.AcceptedCount, batch.RejectedCount, 0, 0, ReadErrors(batch.ErrorsJson));
    }

    private PreparedImport Prepare(ValidatedRequest request, string batchId, string sourceHash, CancellationToken cancellationToken)
    {
        var collection = catalog.Collections().Single(item => item.Id == request.CollectionId);
        var fields = catalog.Fields(collection.Id).Where(field => field.ArchivedAt is null).ToDictionary(field => field.Id, StringComparer.Ordinal);
        var source = ReadSource(request, cancellationToken);
        var mapping = request.Mappings.ToDictionary(item => item.SourceColumn, StringComparer.OrdinalIgnoreCase);
        var mappedFields = request.Mappings.ToDictionary(item => item.FieldId, item => item, StringComparer.Ordinal);
        var configurationErrors = request.Mappings.Where(item => !fields.ContainsKey(item.FieldId)).Select(item =>
            new ImportRowError(0, "FIELD_NOT_FOUND", item.SourceColumn, item.FieldId, "目标字段不存在。"))
            .Concat(request.Mappings.Where(item => fields.TryGetValue(item.FieldId, out var field) && field.StorageKind != "Custom").Select(item =>
                new ImportRowError(0, "SYSTEM_FIELD_IMPORT_FORBIDDEN", item.SourceColumn, item.FieldId, "系统字段必须通过领域命令修改，不能由表格导入写入。")))
            .ToArray();
        var unknownColumns = source.Columns.Where(column => !mapping.ContainsKey(column)).ToArray();
        var missingRequired = fields.Values.Where(field => field.Required && field.ArchivedAt is null && !mappedFields.ContainsKey(field.Id)).ToArray();
        var stableMapping = request.Mappings.Where(item => item.StableKey).ToArray();
        var errors = new List<ImportRowError>(configurationErrors);
        if (unknownColumns.Length != 0)
            errors.AddRange(unknownColumns.Select(column => new ImportRowError(0, "UNMAPPED_COLUMN", column, null, "源列没有明确的目标字段映射。")));
        if (missingRequired.Length != 0)
            errors.AddRange(missingRequired.Select(field => new ImportRowError(0, "REQUIRED_FIELD_UNMAPPED", null, field.Id, "必填字段没有映射。")));

        var rows = new List<ImportPreviewRow>(source.Rows.Count);
        var writes = new List<ImportedWriteRow>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var existingByKey = stableMapping.Length == 1 && configurationErrors.Length == 0
            ? LoadStableRecords(request.CollectionId, fields[stableMapping[0].FieldId])
            : new Dictionary<string, StableRecord>(StringComparer.Ordinal);
        foreach (var sourceRow in source.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rowErrors = new List<ImportRowError>(errors.Select(error => error with { RowNumber = sourceRow.RowNumber }));
            if (sourceRow.Values.Count != source.Columns.Count)
            {
                rowErrors.Add(new ImportRowError(sourceRow.RowNumber, "ROW_COLUMN_COUNT", null, null, "该行列数与表头不一致。"));
            }
            var values = source.Columns.Select((column, index) => new KeyValuePair<string, string?>(column,
                index < sourceRow.Values.Count ? sourceRow.Values[index] : null)).ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
            var cells = new List<ImportedWriteCell>();
            string? stableKey = null;
            foreach (var importMapping in request.Mappings)
            {
                if (!fields.TryGetValue(importMapping.FieldId, out var field)) continue;
                var raw = values.TryGetValue(importMapping.SourceColumn, out var sourceValue) ? sourceValue?.Trim() : null;
                if (importMapping.StableKey)
                {
                    stableKey = CanonicalRawKey(field, raw);
                    if (stableKey is null) rowErrors.Add(new ImportRowError(sourceRow.RowNumber, "STABLE_KEY_REQUIRED", importMapping.SourceColumn, field.Id, "稳定键不能为空。"));
                    else if (!seenKeys.Add(stableKey)) rowErrors.Add(new ImportRowError(sourceRow.RowNumber, "DUPLICATE_INPUT_KEY", importMapping.SourceColumn, field.Id, "输入文件中重复出现同一稳定键。"));
                }
                var parsed = field.Type is "Choice" or "MultiChoice"
                    ? TryParseChoiceValue(field, raw, out var value, out var errorCode, out var errorMessage)
                    : TryParseValue(field, raw, out value, out errorCode, out errorMessage);
                if (!parsed)
                {
                    rowErrors.Add(new ImportRowError(sourceRow.RowNumber, errorCode!, importMapping.SourceColumn, field.Id, errorMessage!));
                    continue;
                }
                if (field.Required && value.IsEmpty)
                    rowErrors.Add(new ImportRowError(sourceRow.RowNumber, "REQUIRED_VALUE", importMapping.SourceColumn, field.Id, "必填字段不能为空。"));
                if (!value.IsEmpty)
                {
                    var relationError = ValidateRelationTargets(field, value);
                    if (relationError is not null) rowErrors.Add(new ImportRowError(sourceRow.RowNumber, relationError, importMapping.SourceColumn, field.Id, "关联目标不存在、已归档或不属于目标集合。"));
                    else cells.Add(new ImportedWriteCell(field.Id, field.Revision, value, $"{batchId}:row:{sourceRow.RowNumber}:field:{field.Id}"));
                }
            }
            if (cells.Count == 0 && rowErrors.Count == 0)
                rowErrors.Add(new ImportRowError(sourceRow.RowNumber, "ROW_EMPTY", null, null, "该行没有可写入的值。"));

            string? recordId = null;
            var create = true;
            var expectedRevision = 0;
            if (rowErrors.Count == 0 && string.Equals(request.DuplicateStrategy, "UpdateByStableKey", StringComparison.Ordinal))
            {
                if (stableKey is null) rowErrors.Add(new ImportRowError(sourceRow.RowNumber, "STABLE_KEY_REQUIRED", null, stableMapping[0].FieldId, "按稳定键更新时必须提供稳定键。"));
                else if (existingByKey.TryGetValue(stableKey, out var existing))
                {
                    if (string.IsNullOrWhiteSpace(existing.Id))
                        rowErrors.Add(new ImportRowError(sourceRow.RowNumber, "DUPLICATE_STABLE_KEY", null, stableMapping[0].FieldId, "工作区中存在多个相同稳定键，无法安全更新。"));
                    else
                    {
                        recordId = existing.Id;
                        create = false;
                        expectedRevision = existing.Revision;
                    }
                }
            }
            recordId ??= "import-" + batchId + "-" + sourceRow.RowNumber.ToString("D8", CultureInfo.InvariantCulture);
            var previewRow = new ImportPreviewRow(sourceRow.RowNumber, values, recordId, create, rowErrors);
            rows.Add(previewRow);
            errors.AddRange(rowErrors);
            if (rowErrors.Count == 0) writes.Add(new ImportedWriteRow(recordId, create, expectedRevision, cells));
        }

        return new PreparedImport(rows, writes, errors, rows.Count(row => row.Accepted), source.Columns, sourceHash);
    }

    private ParsedSource ReadSource(ValidatedRequest request, CancellationToken cancellationToken)
    {
        return request.Format switch
        {
            "csv" => ReadCsv(request.SourcePath, request.MaxRows, cancellationToken),
            "xlsx" => ReadXlsx(request, cancellationToken),
            _ => throw new InvalidOperationException("IMPORT_FORMAT_UNSUPPORTED")
        };
    }

    private static ParsedSource ReadCsv(string path, int maxRows, CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(path);
        using var reader = new StreamReader(stream, DetectEncoding(stream), true);
        var rows = new List<IReadOnlyList<string?>>();
        IReadOnlyList<string?>? header = null;
        var rowNumber = 0;
        while (TryReadCsvRow(reader, out var row, out var malformed))
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowNumber++;
            if (malformed) throw new InvalidDataException("IMPORT_CSV_MALFORMED");
            if (header is null)
            {
                header = row;
                ValidateHeaders(header);
                continue;
            }
            if (rows.Count >= maxRows) throw new InvalidDataException("IMPORT_ROW_LIMIT");
            rows.Add(row);
        }
        if (header is null || header.Count == 0) throw new InvalidDataException("IMPORT_HEADER_MISSING");
        return new ParsedSource(header.Select(item => item ?? "").ToArray(), rows.Select((values, index) => new SourceRow(index + 2, values)).ToArray());
    }

    private ParsedSource ReadXlsx(ValidatedRequest request, CancellationToken cancellationToken)
    {
        var staging = Path.Combine(Path.GetTempPath(), "tools-touch-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var requestPath = Path.Combine(staging, "request.json");
            File.WriteAllText(requestPath, JsonSerializer.Serialize(new XlsxImportRequest(1, request.SourcePath, request.SheetName), JsonOptions));
            RunXlsxImporter(requestPath, staging, cancellationToken);
            var manifestPath = Path.Combine(staging, "manifest.json");
            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = manifest.RootElement;
            var columns = root.GetProperty("columns").EnumerateArray().Select(item => item.GetString() ?? "").ToArray();
            ValidateHeaders(columns);
            var expected = root.GetProperty("row_count").GetInt32();
            var rowsPath = ResolveStaging(staging, root.GetProperty("rows_path").GetString() ?? "");
            var rows = new List<SourceRow>();
            using var stream = File.OpenRead(rowsPath);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lineNumber = 0;
            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = reader.ReadLine() ?? "";
                lineNumber++;
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("IMPORT_XLSX_ROW_INVALID");
                var values = document.RootElement.EnumerateArray().Select(ToText).ToArray();
                rows.Add(new SourceRow(lineNumber + 1, values));
                if (rows.Count > request.MaxRows) throw new InvalidDataException("IMPORT_ROW_LIMIT");
            }
            if (rows.Count != expected) throw new InvalidDataException("IMPORT_XLSX_ROW_COUNT");
            return new ParsedSource(columns, rows);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    private void RunXlsxImporter(string requestPath, string outputDirectory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo { FileName = pythonPath, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        var isUv = Path.GetFileNameWithoutExtension(pythonPath).Equals("uv", StringComparison.OrdinalIgnoreCase);
        if (isUv)
        {
            start.ArgumentList.Add("run"); start.ArgumentList.Add("--all-packages"); start.ArgumentList.Add("--locked"); start.ArgumentList.Add("python");
        }
        start.ArgumentList.Add("-m"); start.ArgumentList.Add("tools_touch_collector"); start.ArgumentList.Add("import-xlsx");
        start.ArgumentList.Add("--request"); start.ArgumentList.Add(requestPath); start.ArgumentList.Add("--output"); start.ArgumentList.Add(outputDirectory);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("XLSX_PROCESS_START_FAILED");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        process.WaitForExit();
        var error = stderr.GetAwaiter().GetResult();
        if (process.ExitCode != 0) throw new InvalidOperationException("XLSX_IMPORT_FAILED: " + error.Trim());
        if (string.IsNullOrWhiteSpace(stdout.GetAwaiter().GetResult())) throw new InvalidDataException("XLSX_IMPORT_RESULT_MISSING");
    }

    private Dictionary<string, StableRecord> LoadStableRecords(string collectionId, FieldDefinitionRecord field)
    {
        var result = new Dictionary<string, StableRecord>(StringComparer.Ordinal);
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            SELECT r.Id,r.Revision,v.TextValue,v.NumberValue,v.DateValue,v.BoolValue
            FROM RecordRef r JOIN FieldValue v ON v.RecordId=r.Id AND v.FieldId=$field
            WHERE r.CollectionId=$collection AND r.ArchivedAt IS NULL
            """, ("$field", field.Id), ("$collection", collectionId));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var key = field.Type switch
            {
                "Text" or "Url" => reader.IsDBNull(2) ? null : reader.GetString(2),
                "Number" => reader.IsDBNull(3) ? null : Convert.ToDecimal(reader.GetDouble(3), CultureInfo.InvariantCulture).ToString("G29", CultureInfo.InvariantCulture),
                "DateTime" => reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture).ToUniversalTime().ToString("O"),
                "Boolean" => reader.IsDBNull(5) ? null : (reader.GetInt64(5) != 0 ? "true" : "false"),
                _ => null
            };
            if (key is null) continue;
            var canonical = field.Type + ":" + key;
            if (result.ContainsKey(canonical)) result[canonical] = new StableRecord("", -1);
            else result[canonical] = new StableRecord(reader.GetString(0), reader.GetInt32(1));
        }
        return result;
    }

    private string? ValidateRelationTargets(FieldDefinitionRecord field, TypedRecordValue value)
    {
        if (field.Type != "Relation" || value.RelationRecordIds is null) return null;
        string? targetCollection = null;
        if (!string.IsNullOrWhiteSpace(field.OptionsJson))
        {
            using var options = JsonDocument.Parse(field.OptionsJson);
            if (options.RootElement.TryGetProperty("target_collection_id", out var target)) targetCollection = target.GetString();
        }
        using var connection = database.Open();
        foreach (var id in value.RelationRecordIds)
        {
            using var command = LocalDatabase.Command(connection, "SELECT CollectionId FROM RecordRef WHERE Id=$id AND ArchivedAt IS NULL", ("$id", id));
            var actual = command.ExecuteScalar() as string;
            if (actual is null || targetCollection is not null && !string.Equals(actual, targetCollection, StringComparison.Ordinal)) return "RELATION_TARGET_NOT_FOUND";
        }
        return null;
    }

    private static bool TryParseValue(FieldDefinitionRecord field, string? raw, out TypedRecordValue value, out string? errorCode, out string? errorMessage)
    {
        value = new TypedRecordValue(field.Type);
        errorCode = null;
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        switch (field.Type)
        {
            case "Text": value = new TypedRecordValue(field.Type, TextValue: raw); return true;
            case "Url":
                if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return Fail(out errorCode, out errorMessage, "INVALID_URL", "URL 必须是 HTTP(S) 地址。");
                value = new TypedRecordValue(field.Type, TextValue: raw); return true;
            case "Number":
                if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return Fail(out errorCode, out errorMessage, "INVALID_NUMBER", "数字格式无效。");
                value = new TypedRecordValue(field.Type, NumberValue: number); return true;
            case "DateTime":
                if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)) return Fail(out errorCode, out errorMessage, "INVALID_DATETIME", "日期时间格式无效。");
                value = new TypedRecordValue(field.Type, DateValue: date); return true;
            case "Boolean":
                if (!TryBoolean(raw, out var boolean)) return Fail(out errorCode, out errorMessage, "INVALID_BOOLEAN", "布尔值仅支持 true／false、yes／no 或 1／0。");
                value = new TypedRecordValue(field.Type, BoolValue: boolean); return true;
            case "Choice":
            case "MultiChoice":
                return Fail(out errorCode, out errorMessage, "CHOICE_LABEL_UNRESOLVED", "选择字段需要通过选项 ID 或标签解析。");
            case "Relation":
                var relations = SplitList(raw);
                value = new TypedRecordValue(field.Type, RelationRecordIds: relations); return true;
            default: return Fail(out errorCode, out errorMessage, "FIELD_TYPE_UNSUPPORTED", "字段类型不受导入支持。");
        }
    }

    private bool TryParseChoiceValue(FieldDefinitionRecord field, string? raw, out TypedRecordValue value, out string? errorCode, out string? errorMessage)
    {
        value = new TypedRecordValue(field.Type);
        errorCode = null;
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        var choices = catalog.Choices(field.Id).Where(choice => choice.ArchivedAt is null).ToArray();
        var tokens = SplitList(raw);
        var ids = new List<string>();
        foreach (var token in tokens)
        {
            var choice = choices.FirstOrDefault(item => string.Equals(item.Id, token, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Label, token, StringComparison.OrdinalIgnoreCase));
            if (choice is null) return Fail(out errorCode, out errorMessage, "CHOICE_NOT_FOUND", "选择标签或 ID 不存在。");
            ids.Add(choice.Id);
        }
        if (field.Type == "Choice" && ids.Count != 1) return Fail(out errorCode, out errorMessage, "SINGLE_CHOICE_REQUIRED", "单选字段只能有一个选项。");
        value = new TypedRecordValue(field.Type, ChoiceIds: ids.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        return true;
    }

    private static bool Fail(out string? errorCode, out string? errorMessage, string code, string message)
    {
        errorCode = code; errorMessage = message; return false;
    }

    private static string[] SplitList(string raw) => raw.Split([';', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static bool TryBoolean(string raw, out bool value)
    {
        if (bool.TryParse(raw, out value)) return true;
        if (raw is "1" or "yes" or "y" or "是") { value = true; return true; }
        if (raw is "0" or "no" or "n" or "否") { value = false; return true; }
        value = false; return false;
    }

    private static string? CanonicalRawKey(FieldDefinitionRecord field, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!TryParseValue(field, raw, out var value, out _, out _)) return null;
        return CanonicalValue(value);
    }

    private static string? CanonicalValue(TypedRecordValue value) => value.Type switch
    {
        "Text" or "Url" => value.TextValue,
        "Number" => value.NumberValue?.ToString("G29", CultureInfo.InvariantCulture),
        "DateTime" => value.DateValue?.ToUniversalTime().ToString("O"),
        "Boolean" => value.BoolValue?.ToString().ToLowerInvariant(),
        _ => null
    } is { } scalar ? value.Type + ":" + scalar : null;

    private ValidatedRequest ValidateRequest(ImportRequest request)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        var collectionId = Required(request.CollectionId, nameof(request.CollectionId));
        var sourcePath = Path.GetFullPath(Required(request.SourcePath, nameof(request.SourcePath)));
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("IMPORT_SOURCE_NOT_FOUND", sourcePath);
        var format = Required(request.Format, nameof(request.Format)).ToLowerInvariant();
        if (format is not ("csv" or "xlsx")) throw new InvalidOperationException("IMPORT_FORMAT_UNSUPPORTED");
        var strategy = Required(request.DuplicateStrategy, nameof(request.DuplicateStrategy));
        if (strategy is not ("Append" or "UpdateByStableKey" or "PreviewOnly")) throw new InvalidOperationException("IMPORT_DUPLICATE_STRATEGY_UNSUPPORTED");
        if (request.MaxRows is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(request.MaxRows));
        var commandId = Required(request.CommandId, nameof(request.CommandId));
        if (request.Mappings is null || request.Mappings.Count == 0) throw new ArgumentException("At least one field mapping is required.", nameof(request.Mappings));
        if (request.Mappings.Any(item => string.IsNullOrWhiteSpace(item.SourceColumn) || string.IsNullOrWhiteSpace(item.FieldId)))
            throw new ArgumentException("Import mappings require source columns and field ids.", nameof(request.Mappings));
        if (request.Mappings.GroupBy(item => item.SourceColumn.Trim(), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidOperationException("IMPORT_SOURCE_COLUMN_DUPLICATE");
        if (request.Mappings.GroupBy(item => item.FieldId.Trim(), StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new InvalidOperationException("IMPORT_FIELD_MAPPING_DUPLICATE");
        if (request.Mappings.Count(item => item.StableKey) > 1) throw new InvalidOperationException("IMPORT_STABLE_KEY_MULTIPLE");
        if (strategy == "UpdateByStableKey" && request.Mappings.Count(item => item.StableKey) != 1)
            throw new InvalidOperationException("IMPORT_STABLE_KEY_REQUIRED");
        var collection = catalog.Collections().FirstOrDefault(item => item.Id == collectionId && item.ArchivedAt is null)
            ?? throw new KeyNotFoundException("COLLECTION_NOT_FOUND");
        return new ValidatedRequest(collectionId, sourcePath, commandId, request.Mappings.Select(item => item with { SourceColumn = item.SourceColumn.Trim(), FieldId = item.FieldId.Trim() }).ToArray(), format, strategy, request.SheetName, request.MaxRows);
    }

    private BatchInfo? FindBatchByCommand(string commandId)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, BatchSql + " WHERE CommandId=$command", ("$command", commandId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBatch(reader) : null;
    }

    private BatchInfo? FindBatch(string id)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, BatchSql + " WHERE Id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBatch(reader) : null;
    }

    private void MarkCommitted(string id, int scanned, int accepted, int rejected)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "UPDATE ImportBatch SET State='Committed',ScannedCount=$scanned,AcceptedCount=$accepted,RejectedCount=$rejected,FinishedAt=$finished WHERE Id=$id AND State='Preview'",
            ("$id", id), ("$scanned", scanned), ("$accepted", accepted), ("$rejected", rejected), ("$finished", Now));
        command.ExecuteNonQuery();
    }

    private void MarkFailed(string id, string error, IReadOnlyList<ImportRowError> errors)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "UPDATE ImportBatch SET State='Failed',Error=$error,ErrorsJson=$errors,FinishedAt=$finished WHERE Id=$id AND State='Preview'",
            ("$id", id), ("$error", error), ("$errors", JsonSerializer.Serialize(errors, JsonOptions)), ("$finished", Now));
        command.ExecuteNonQuery();
    }

    private static ImportPreviewResult ToPreview(string batchId, ValidatedRequest request, string sourceHash, PreparedImport prepared) =>
        new(batchId, request.CollectionId, request.Format, request.Format, request.DuplicateStrategy, sourceHash,
            prepared.Rows.Count, prepared.AcceptedCount, prepared.Rows.Count - prepared.AcceptedCount, prepared.Columns, prepared.Rows, prepared.Errors);

    private static ImportCommitResult ToCommit(BatchInfo batch, IReadOnlyList<ImportRowError> errors) =>
        new(batch.Id, batch.State, batch.ScannedCount, batch.AcceptedCount, batch.RejectedCount, 0, 0, errors);

    private static string ErrorCode(string message) => message.Split(' ', ':', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "IMPORT_COMMIT_FAILED";
    private static IReadOnlyList<ImportRowError> ReadErrors(string? json) => string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<IReadOnlyList<ImportRowError>>(json, JsonOptions) ?? [];
    private static string Required(string? value, string name) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A value is required.", name) : value.Trim();
    private static string Now => DateTimeOffset.UtcNow.ToString("O");
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string? ToText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetRawText(),
        _ => value.GetRawText()
    };

    private static Encoding DetectEncoding(Stream stream)
    {
        Span<byte> prefix = stackalloc byte[3];
        var count = stream.Read(prefix);
        stream.Position = 0;
        if (count >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF) return new UTF8Encoding(true, true);
        if (count >= 2 && prefix[0] == 0xFF && prefix[1] == 0xFE) return new UnicodeEncoding(false, true, true);
        if (count >= 2 && prefix[0] == 0xFE && prefix[1] == 0xFF) return new UnicodeEncoding(true, true, true);
        return new UTF8Encoding(false, true);
    }

    private static bool TryReadCsvRow(TextReader reader, out IReadOnlyList<string?> row, out bool malformed)
    {
        var values = new List<string?>();
        var value = new StringBuilder();
        var quoted = false;
        var started = false;
        var readAny = false;
        while (true)
        {
            var next = reader.Read();
            if (next < 0)
            {
                if (!readAny && value.Length == 0 && values.Count == 0) { row = []; malformed = false; return false; }
                if (quoted) { row = []; malformed = true; return true; }
                values.Add(value.ToString()); row = values; malformed = false; return true;
            }
            readAny = true;
            var character = (char)next;
            if (quoted)
            {
                if (character == '"')
                {
                    var peek = reader.Peek();
                    if (peek == '"') { reader.Read(); value.Append('"'); }
                    else quoted = false;
                }
                else value.Append(character);
                continue;
            }
            if (character == '"' && !started) { quoted = true; started = true; continue; }
            if (character == ',') { values.Add(value.ToString()); value.Clear(); started = false; continue; }
            if (character == '\n' || character == '\r')
            {
                if (character == '\r' && reader.Peek() == '\n') reader.Read();
                values.Add(value.ToString()); row = values; malformed = false; return true;
            }
            started = true; value.Append(character);
        }
    }

    private static void ValidateHeaders(IReadOnlyList<string?> headers)
    {
        if (headers.Count == 0 || headers.Any(item => string.IsNullOrWhiteSpace(item))) throw new InvalidDataException("IMPORT_HEADER_INVALID");
        if (headers.Select(item => item!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != headers.Count) throw new InvalidDataException("IMPORT_HEADER_DUPLICATE");
    }

    private static string ResolveStaging(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(part => part is "" or "." or "..")) throw new InvalidDataException("IMPORT_XLSX_PATH_INVALID");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("IMPORT_XLSX_PATH_INVALID");
        return path;
    }

    private const string BatchSql = "SELECT Id,SourceKind,State,CommandId,ScannedCount,AcceptedCount,RejectedCount,CollectionId,SourcePath,SourceHash,Format,DuplicateStrategy,SheetName,MappingJson,ErrorsJson,Error FROM ImportBatch";

    private static BatchInfo ReadBatch(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6),
        Nullable(reader, 7), Nullable(reader, 8), Nullable(reader, 9), Nullable(reader, 10), Nullable(reader, 11), Nullable(reader, 12), Nullable(reader, 13), Nullable(reader, 14), Nullable(reader, 15));
    private static string? Nullable(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);

    private sealed record ValidatedRequest(string CollectionId, string SourcePath, string CommandId, IReadOnlyList<ImportFieldMapping> Mappings,
        string Format, string DuplicateStrategy, string? SheetName, int MaxRows);
    private sealed record ParsedSource(IReadOnlyList<string> Columns, IReadOnlyList<SourceRow> Rows);
    private sealed record SourceRow(int RowNumber, IReadOnlyList<string?> Values);
    private sealed record PreparedImport(IReadOnlyList<ImportPreviewRow> Rows, IReadOnlyList<ImportedWriteRow> WriteRows,
        IReadOnlyList<ImportRowError> Errors, int AcceptedCount, IReadOnlyList<string> Columns, string SourceHash);
    private sealed record StableRecord(string Id, int Revision);
    private sealed record BatchInfo(string Id, string SourceKind, string State, string CommandId, int ScannedCount, int AcceptedCount, int RejectedCount,
        string? CollectionId, string? SourcePath, string? SourceHash, string? Format, string? DuplicateStrategy, string? SheetName, string? MappingJson, string? ErrorsJson, string? Error)
    {
        public BatchInfo With(string? state = null, string? error = null, string? errorsJson = null, int? scannedCount = null, int? acceptedCount = null, int? rejectedCount = null) =>
            this with { State = state ?? State, Error = error ?? Error, ErrorsJson = errorsJson ?? ErrorsJson, ScannedCount = scannedCount ?? ScannedCount,
                AcceptedCount = acceptedCount ?? AcceptedCount, RejectedCount = rejectedCount ?? RejectedCount };
    }
    private sealed record XlsxImportRequest(int SchemaVersion, string SourcePath, string? SheetName);
}
