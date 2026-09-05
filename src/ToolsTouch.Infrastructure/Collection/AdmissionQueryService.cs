using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ToolsTouch.Application;
using ToolsTouch.Core;

namespace ToolsTouch.Infrastructure.Collection;

public sealed class AdmissionQueryService(LocalDatabase database) : IAdmissionQueryService
{
    private readonly WindowEvaluator evaluator = new();

    public PageResult<SchoolOverviewRow> ListSchools(int targetCycleYear, string timeZoneId, DateTimeOffset asOf, PageRequest request)
    {
        var rows = BuildRows(null, targetCycleYear, timeZoneId, asOf, out var revision)
            .GroupBy(row => new { row.SchoolId, row.SchoolName })
            .Select(group => Summarize(group.Key.SchoolId, group.Key.SchoolName, group.ToArray(), revision))
            .OrderBy(row => row.SchoolName, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.SchoolId, StringComparer.Ordinal)
            .ToArray();
        var cursor = Decode<SchoolCursor>(request.After);
        if (cursor is not null) rows = rows.Where(row => Compare(row.SchoolName, row.SchoolId, cursor.Name, cursor.Id) > 0).ToArray();
        return Page(rows, request.EffectiveLimit, row => new SchoolCursor(row.SchoolName, row.SchoolId));
    }

    public PageResult<AdmissionRoundRow> ListRounds(string schoolId, int targetCycleYear, string timeZoneId, DateTimeOffset asOf, PageRequest request)
    {
        var rows = BuildRows(schoolId, targetCycleYear, timeZoneId, asOf, out _)
            .OrderBy(row => row.DepartmentName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ProgramName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Kind)
            .ThenBy(row => row.RoundKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.RoundId, StringComparer.Ordinal)
            .ToArray();
        var cursor = Decode<RoundCursor>(request.After);
        if (cursor is not null) rows = rows.Where(row => Compare(row, cursor) > 0).ToArray();
        return Page(rows, request.EffectiveLimit, row => new RoundCursor(row.DepartmentName, row.ProgramName, row.Kind.ToString(), row.RoundKey, row.RoundId));
    }

    public string ExportRoundsCsv(string schoolId, int targetCycleYear, string timeZoneId, DateTimeOffset asOf)
    {
        var rows = BuildRows(schoolId, targetCycleYear, timeZoneId, asOf, out _)
            .OrderBy(row => row.DepartmentName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.ProgramName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Kind)
            .ThenBy(row => row.RoundKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.RoundId, StringComparer.Ordinal)
            .ToArray();
        var builder = new StringBuilder();
        builder.AppendLine(string.Join(',', "school", "department", "program", "degree_type", "kind", "title", "round_id",
            "actual_state", "actual_basis", "estimated_phase", "start", "end_exclusive", "projected_start",
            "projected_end_exclusive", "official_url", "application_url", "reasons", "evidence_ids"));
        foreach (var row in rows)
        {
            var assessment = row.Assessment;
            builder.AppendLine(string.Join(',', new[]
            {
                row.SchoolName, row.DepartmentName, row.ProgramName, row.DegreeType, row.Kind.ToString(), row.Title, row.RoundId,
                assessment.ActualState.ToString(), assessment.ActualBasis.ToString(), assessment.EstimatedPhase.ToString(),
                Format(assessment.Start), Format(assessment.EndExclusive), Format(assessment.ProjectedStart), Format(assessment.ProjectedEndExclusive),
                row.OfficialUrl, row.ApplicationUrl, string.Join(';', assessment.Reasons), string.Join(';', assessment.EvidenceIds)
            }.Select(Escape)));
        }
        return builder.ToString();
    }

    private IReadOnlyList<AdmissionRoundRow> BuildRows(string? schoolId, int targetCycleYear, string timeZoneId,
        DateTimeOffset asOf, out long revision)
    {
        if (targetCycleYear is < 1900 or > 2200) throw new ArgumentOutOfRangeException(nameof(targetCycleYear));
        if (string.IsNullOrWhiteSpace(timeZoneId)) throw new ArgumentException("TIME_ZONE_REQUIRED", nameof(timeZoneId));
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        revision = Convert.ToInt64(Scalar(connection, transaction, "SELECT DataRevision FROM WorkspaceMeta WHERE Id=1"));
        using var command = LocalDatabase.Command(connection, """
            SELECT r.Id,r.ProgramId,r.CycleYear,r.EntryYear,r.Kind,r.RoundKey,r.Title,r.OfficialUrl,r.ApplicationUrl,
                   p.Name,p.DegreeType,d.Id,d.CanonicalName,s.Id,s.CanonicalName,
                   o.Id,o.SourceSnapshotId,o.SourceYear,o.CycleYear,o.EntryYear,o.RegistrationStartRaw,o.RegistrationEndRaw,
                   o.Precision,o.TimeZone,o.ObservedAt
            FROM AdmissionRound r
            JOIN AdmissionProgram p ON p.Id=r.ProgramId
            JOIN Department d ON d.Id=p.DepartmentId
            JOIN School s ON s.Id=d.SchoolId
            LEFT JOIN WindowObservation o ON o.RoundId=r.Id
            WHERE r.ArchivedAt IS NULL AND p.ArchivedAt IS NULL AND d.ArchivedAt IS NULL AND s.ArchivedAt IS NULL
              AND ($school IS NULL OR s.Id=$school)
            ORDER BY s.CanonicalName COLLATE NOCASE,s.Id,d.CanonicalName COLLATE NOCASE,d.Id,p.Name COLLATE NOCASE,p.Id,r.RoundKey,r.Id,o.ObservedAt DESC,o.Id DESC
            """, ("$school", schoolId));
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        var rounds = new Dictionary<string, StoredRound>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var roundId = reader.GetString(0);
            if (!rounds.TryGetValue(roundId, out var round))
            {
                round = new StoredRound(roundId, reader.GetString(13), reader.GetString(14), reader.GetString(11), reader.GetString(12),
                    reader.GetString(1), reader.GetString(9), reader.GetString(10), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    NullableString(reader, 7), NullableString(reader, 8), []);
                rounds.Add(roundId, round);
            }
            if (!reader.IsDBNull(15)) round.Observations.Add(new StoredObservation(reader.GetString(15), reader.GetString(16), NullableInt(reader, 17),
                NullableInt(reader, 18), NullableInt(reader, 19), NullableString(reader, 20), NullableString(reader, 21),
                Enum.Parse<DatePrecision>(reader.GetString(22)), NullableString(reader, 23), DateTimeOffset.Parse(reader.GetString(24))));
        }
        transaction.Commit();
        return rounds.Values.Select(round => ToRow(round, targetCycleYear, timeZoneId, asOf)).ToArray();
    }

    private AdmissionRoundRow ToRow(StoredRound round, int targetCycleYear, string timeZoneId, DateTimeOffset asOf)
    {
        var current = round.Observations.Where(observation => observation.CycleYear == targetCycleYear)
            .OrderByDescending(observation => observation.ObservedAt).ThenByDescending(observation => observation.Id).FirstOrDefault();
        var historical = round.Observations.Where(observation => observation.CycleYear is not null && observation.CycleYear < targetCycleYear)
            .OrderByDescending(observation => observation.CycleYear).ThenByDescending(observation => observation.ObservedAt).ThenByDescending(observation => observation.Id).FirstOrDefault();
        var currentWindow = current is null ? null : new CurrentWindow(targetCycleYear, ActualWindowBasis.CurrentAggregator,
            new RegistrationRange(current.StartRaw, current.EndRaw, current.Precision), EvidenceIds: [current.SnapshotId]);
        var historicalWindow = historical is null ? null : new HistoricalWindow(historical.CycleYear!.Value,
            new RegistrationRange(historical.StartRaw, historical.EndRaw, historical.Precision), [historical.SnapshotId]);
        var assessment = evaluator.Evaluate(new WindowInput(targetCycleYear, timeZoneId, currentWindow, historicalWindow), asOf);
        return new(round.Id, round.SchoolId, round.SchoolName, round.DepartmentId, round.DepartmentName, round.ProgramId,
            round.ProgramName, round.DegreeType, Enum.Parse<AdmissionRoundKind>(round.Kind), round.RoundKey, round.Title,
            round.OfficialUrl, round.ApplicationUrl, assessment);
    }

    private static SchoolOverviewRow Summarize(string schoolId, string schoolName, AdmissionRoundRow[] rows, long revision)
    {
        static int DistinctDepartments(IEnumerable<AdmissionRoundRow> values) => values.Select(row => row.DepartmentId).Distinct(StringComparer.Ordinal).Count();
        var open = rows.Where(row => row.Assessment.ActualState == ActualWindowState.Open).ToArray();
        var upcoming = rows.Where(row => row.Assessment.ActualState == ActualWindowState.NotStarted).ToArray();
        var forecast = rows.Where(row => row.Assessment.ActualState == ActualWindowState.Unknown && row.Assessment.EstimatedPhase != EstimatedWindowPhase.NotAvailable).ToArray();
        var unknown = rows.Count(row => row.Assessment.ActualState == ActualWindowState.Unknown && row.Assessment.EstimatedPhase == EstimatedWindowPhase.NotAvailable);
        var closed = rows.Count(row => row.Assessment.ActualState == ActualWindowState.Closed);
        var complete = rows.Length > 0 && rows.All(row => row.Assessment.ActualState is ActualWindowState.Closed or ActualWindowState.Cancelled);
        return new(schoolId, schoolName, DistinctDepartments(open), open.Length, DistinctDepartments(upcoming), DistinctDepartments(forecast),
            unknown, closed, rows.Length, complete, revision);
    }

    private static int Compare(string leftName, string leftId, string rightName, string rightId)
    {
        var result = StringComparer.OrdinalIgnoreCase.Compare(leftName, rightName);
        return result != 0 ? result : StringComparer.Ordinal.Compare(leftId, rightId);
    }

    private static int Compare(AdmissionRoundRow row, RoundCursor cursor)
    {
        var result = StringComparer.OrdinalIgnoreCase.Compare(row.DepartmentName, cursor.Department);
        if (result != 0) return result;
        result = StringComparer.OrdinalIgnoreCase.Compare(row.ProgramName, cursor.Program);
        if (result != 0) return result;
        result = StringComparer.Ordinal.Compare(row.Kind.ToString(), cursor.Kind);
        if (result != 0) return result;
        result = StringComparer.OrdinalIgnoreCase.Compare(row.RoundKey, cursor.RoundKey);
        return result != 0 ? result : StringComparer.Ordinal.Compare(row.RoundId, cursor.Id);
    }

    private static PageResult<T> Page<T, TCursor>(T[] items, int limit, Func<T, TCursor> cursor)
    {
        string? next = null;
        if (items.Length > limit)
        {
            var page = items[..limit];
            next = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor(page[^1])));
            return new(page, next);
        }
        return new(items, next);
    }

    private static T? Decode<T>(string? value)
    {
        if (value is null) return default;
        try { return JsonSerializer.Deserialize<T>(Convert.FromBase64String(value)); }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
        { throw new ArgumentException("Invalid page cursor.", nameof(value), error); }
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql; command.Transaction = transaction; return command.ExecuteScalar();
    }

    private static string Escape(string? value) => value is null ? "" : value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    private static string Format(DateTimeOffset? value) => value?.ToString("O") ?? "";
    private static string? NullableString(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static int? NullableInt(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt32(index);

    private sealed class StoredRound(string id, string schoolId, string schoolName, string departmentId, string departmentName,
        string programId, string programName, string degreeType, string kind, string roundKey, string title, string? officialUrl, string? applicationUrl,
        List<StoredObservation> observations)
    {
        public string Id { get; } = id;
        public string SchoolId { get; } = schoolId;
        public string SchoolName { get; } = schoolName;
        public string DepartmentId { get; } = departmentId;
        public string DepartmentName { get; } = departmentName;
        public string ProgramId { get; } = programId;
        public string ProgramName { get; } = programName;
        public string DegreeType { get; } = degreeType;
        public string Kind { get; } = kind;
        public string RoundKey { get; } = roundKey;
        public string Title { get; } = title;
        public string? OfficialUrl { get; } = officialUrl;
        public string? ApplicationUrl { get; } = applicationUrl;
        public List<StoredObservation> Observations { get; } = observations;
    }

    private sealed record StoredObservation(string Id, string SnapshotId, int? SourceYear, int? CycleYear, int? EntryYear,
        string? StartRaw, string? EndRaw, DatePrecision Precision, string? TimeZone, DateTimeOffset ObservedAt);
    private sealed record SchoolCursor(string Name, string Id);
    private sealed record RoundCursor(string Department, string Program, string Kind, string RoundKey, string Id);
}
