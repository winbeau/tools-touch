using System.Text.Json;
using Microsoft.Data.Sqlite;
using ToolsTouch.Application;
using ToolsTouch.Core;

namespace ToolsTouch.Infrastructure.Collection;

public sealed class FacultyQueryService(LocalDatabase database) : IFacultyQueryService
{
    public PageResult<FacultyListRow> ListFaculty(string? filter, PageRequest request)
    {
        var cursor = DecodeCursor(request.After);
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id,Name,Institution,Homepage,Email,EvidenceJson
            FROM Professor
            WHERE ($filter='' OR instr(lower(Name || ' ' || Institution || ' ' || EvidenceJson),lower($filter))>0)
            """ + (cursor is null ? "" : " AND (Name COLLATE NOCASE > $name COLLATE NOCASE OR (Name COLLATE NOCASE = $name COLLATE NOCASE AND Id > $id))") +
            " ORDER BY Name COLLATE NOCASE,Id LIMIT $limit";
        command.Parameters.AddWithValue("$filter", filter?.Trim() ?? "");
        command.Parameters.AddWithValue("$limit", request.EffectiveLimit + 1);
        if (cursor is not null)
        {
            command.Parameters.AddWithValue("$name", cursor.Name);
            command.Parameters.AddWithValue("$id", cursor.Id);
        }
        using var reader = command.ExecuteReader();
        var professors = new List<Professor>();
        while (reader.Read()) professors.Add(ReadProfessor(reader));
        reader.Close();
        var rows = professors.Select(professor => BuildRow(connection, professor)).ToList();
        string? next = null;
        if (rows.Count > request.EffectiveLimit)
        {
            rows.RemoveAt(rows.Count - 1);
            next = EncodeCursor(new Cursor(rows[^1].Name, rows[^1].ProfessorId));
        }
        return new PageResult<FacultyListRow>(rows, next);
    }

    public FacultyDetail GetDetail(string professorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(professorId);
        using var connection = database.Open();
        var professor = ReadProfessor(connection, professorId);
        var row = BuildRow(connection, professor);
        var appointments = new List<FacultyAppointmentRow>();
        using (var command = LocalDatabase.Command(connection, """
            SELECT a.Id,d.CanonicalName,s.CanonicalName,a.Title,a.Role,a.IsPrimary,a.EvidenceClaimId
            FROM Appointment a JOIN Department d ON d.Id=a.DepartmentId JOIN School s ON s.Id=d.SchoolId
            WHERE a.ProfessorId=$prof AND a.ArchivedAt IS NULL ORDER BY s.CanonicalName,d.CanonicalName,a.Id
            """, ("$prof", professorId)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) appointments.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                Nullable(reader, 3), Nullable(reader, 4), reader.GetInt64(5) != 0, Nullable(reader, 6)));
        }
        var papers = new List<Paper>();
        using (var command = LocalDatabase.Command(connection, """
            SELECT p.Id,p.ExternalId,p.Title,p.AuthorsJson,p.Year,p.Abstract,p.SourceUrl,p.LocalPath,p.ParseStatus
            FROM Paper p JOIN ProfessorPaper pp ON pp.PaperId=p.Id WHERE pp.ProfessorId=$prof ORDER BY p.Year DESC,p.Title
            """, ("$prof", professorId)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) papers.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                JsonSerializer.Deserialize<string[]>(reader.GetString(3)) ?? [], NullableInt(reader, 4), Nullable(reader, 5),
                reader.GetString(6), Nullable(reader, 7), reader.GetString(8)));
        }
        var evidence = new List<FacultyEvidenceRow>();
        using (var command = LocalDatabase.Command(connection, """
            SELECT Id,ClaimType,ValueJson,QuotedText,LocatorJson,OriginKind,ConfidenceLabel
            FROM EvidenceClaim WHERE SubjectKind='Professor' AND SubjectId=$prof ORDER BY Id
            """, ("$prof", professorId)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) evidence.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                Nullable(reader, 3), Nullable(reader, 4), reader.GetString(5), reader.GetString(6)));
        }
        var evaluations = new List<FacultyEvaluationRow>();
        using (var command = LocalDatabase.Command(connection, """
            SELECT Id,SourceType,PostedAt,Summary,TopicsJson,VerificationState
            FROM ProfessorEvaluation WHERE ProfessorId=$prof ORDER BY CreatedAt DESC,Id
            """, ("$prof", professorId)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) evaluations.Add(new(reader.GetString(0), reader.GetString(1), NullableDate(reader, 2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        }
        var paperRows = papers.Select(paper => new FacultyPaperRow(paper, ReadSegments(connection, paper.Id))).ToArray();
        return new FacultyDetail(row, appointments, paperRows, evidence, evaluations);
    }

    public IReadOnlyList<FacultyCoverageRow> ListCoverage()
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            SELECT Id,ScopeJson,State,Complete,ScannedCount,SelectedCount,ExpectedCount,Error,UpdatedAt
            FROM CrawlBatch WHERE SourceKey='official-faculty-directory' ORDER BY UpdatedAt DESC,Id
            """);
        using var reader = command.ExecuteReader();
        var rawRows = new List<CoverageData>();
        while (reader.Read())
        {
            var scope = JsonSerializer.Deserialize<FacultyDirectoryScope>(reader.GetString(1),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            rawRows.Add(new(reader.GetString(0), scope?.SchoolId ?? "", scope?.DepartmentId ?? "", reader.GetString(2),
                reader.GetInt32(3) != 0, reader.GetInt32(4), reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetInt32(6),
                Nullable(reader, 7), DateTimeOffset.Parse(reader.GetString(8))));
        }
        reader.Close();
        return rawRows.Select(row => new FacultyCoverageRow(row.BatchId, row.SchoolId, row.DepartmentId, row.State,
            row.Complete ? FacultyCoverageState.CompleteForDeclaredSources : row.ExpectedCount is null ? FacultyCoverageState.UnknownDenominator : FacultyCoverageState.Partial,
            CountItems(connection, row.BatchId), row.ScannedCount, row.SelectedCount, row.ExpectedCount, row.Error, row.UpdatedAt)).ToArray();
    }

    private static FacultyListRow BuildRow(SqliteConnection connection, Professor professor)
    {
        var departments = new List<string>();
        var appointmentCount = 0;
        using (var command = LocalDatabase.Command(connection, """
            SELECT d.CanonicalName FROM Appointment a JOIN Department d ON d.Id=a.DepartmentId
            WHERE a.ProfessorId=$prof AND a.ArchivedAt IS NULL ORDER BY d.CanonicalName
            """, ("$prof", professor.Id)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) { appointmentCount++; departments.Add(reader.GetString(0)); }
        }
        DateTimeOffset? lastEvidence = null;
        using (var command = LocalDatabase.Command(connection, """
            SELECT MAX(s.FetchedAt) FROM EvidenceClaim c JOIN SourceSnapshot s ON s.Id=c.SnapshotId
            WHERE c.SubjectKind='Professor' AND c.SubjectId=$prof
            """, ("$prof", professor.Id)))
        {
            var value = command.ExecuteScalar() as string;
            if (value is not null) lastEvidence = DateTimeOffset.Parse(value);
        }
        return new FacultyListRow(professor.Id, professor.Name, professor.Institution, professor.Homepage, professor.Email,
            departments.Distinct(StringComparer.Ordinal).ToArray(), appointmentCount, lastEvidence);
    }

    private static Professor ReadProfessor(SqliteConnection connection, string professorId)
    {
        using var command = LocalDatabase.Command(connection,
            "SELECT Id,Name,Institution,Homepage,Email,EvidenceJson FROM Professor WHERE Id=$id", ("$id", professorId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("PROFESSOR_NOT_FOUND");
        return ReadProfessor(reader);
    }

    private static Professor ReadProfessor(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
        Nullable(reader, 3), Nullable(reader, 4), JsonSerializer.Deserialize<Evidence[]>(reader.GetString(5)) ?? []);

    private static int CountItems(SqliteConnection connection, string batchId)
    {
        using var command = LocalDatabase.Command(connection, "SELECT COUNT(*) FROM CrawlItem WHERE BatchId=$batch", ("$batch", batchId));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static IReadOnlyList<FacultyPaperReadRow> ReadSegments(SqliteConnection connection, string paperId)
    {
        using var command = LocalDatabase.Command(connection, """
            SELECT Id,ContentHash,StartPage,EndPage,ReadScope,ExtractorVersion,TextArtifactId,CreatedAt
            FROM PaperReadSegment WHERE PaperId=$paper ORDER BY CreatedAt,Id
            """, ("$paper", paperId));
        using var reader = command.ExecuteReader();
        var rows = new List<FacultyPaperReadRow>();
        while (reader.Read())
            rows.Add(new(reader.GetString(0), Nullable(reader, 1), NullableInt(reader, 2), NullableInt(reader, 3),
                reader.GetString(4), reader.GetString(5), Nullable(reader, 6), DateTimeOffset.Parse(reader.GetString(7))));
        return rows;
    }

    private static Cursor? DecodeCursor(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        try { return JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(encoded)); }
        catch (Exception error) when (error is FormatException or JsonException) { throw new InvalidOperationException("FACULTY_CURSOR_INVALID", error); }
    }

    private static string EncodeCursor(Cursor cursor) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor));
    private static string? Nullable(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static int? NullableInt(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt32(index);
    private static DateTimeOffset? NullableDate(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : DateTimeOffset.Parse(reader.GetString(index));
    private sealed record Cursor(string Name, string Id);
    private sealed record CoverageData(string BatchId, string SchoolId, string DepartmentId, string State, bool Complete,
        int ScannedCount, int SelectedCount, int? ExpectedCount, string? Error, DateTimeOffset UpdatedAt);
}
