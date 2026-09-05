using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ToolsTouch.Application;

namespace ToolsTouch.Core;

public sealed class OrganizationRepository(LocalDatabase database) : IOrganizationCatalog, IOrganizationWriter, IWorkspaceMetadata
{
    private static string Now => DateTimeOffset.UtcNow.ToString("O");

    public WorkspaceMetadata Get()
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "SELECT WorkspaceId,DataRevision,SchemaVersion FROM WorkspaceMeta WHERE Id=1");
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("WORKSPACE_META_MISSING");
        return new WorkspaceMetadata(reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2));
    }

    public School AddSchool(School school) => Write(school, (connection, transaction) =>
    {
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO School(Id,CanonicalName,ShortName,OfficialCode,Campus,City,Revision,CreatedAt,UpdatedAt)
            VALUES($id,$name,$short,$code,$campus,$city,$revision,$now,$now)
            """, ("$id", school.Id), ("$name", school.CanonicalName), ("$short", school.ShortName),
            ("$code", school.OfficialCode), ("$campus", school.Campus), ("$city", school.City), ("$revision", school.Revision), ("$now", Now));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    });

    public Department AddDepartment(Department department) => Write(department, (connection, transaction) =>
    {
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO Department(Id,SchoolId,CanonicalName,OfficialCode,ParentDepartmentId,Kind,Revision,CreatedAt,UpdatedAt)
            VALUES($id,$school,$name,$code,$parent,$kind,$revision,$now,$now)
            """, ("$id", department.Id), ("$school", department.SchoolId), ("$name", department.CanonicalName),
            ("$code", department.OfficialCode), ("$parent", department.ParentDepartmentId), ("$kind", department.Kind),
            ("$revision", department.Revision), ("$now", Now));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    });

    public AdmissionProgram AddProgram(AdmissionProgram program) => Write(program, (connection, transaction) =>
    {
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO AdmissionProgram(Id,DepartmentId,Name,DegreeType,DisciplineCode,Track,Campus,Revision,CreatedAt,UpdatedAt)
            VALUES($id,$department,$name,$degree,$discipline,$track,$campus,$revision,$now,$now)
            """, ("$id", program.Id), ("$department", program.DepartmentId), ("$name", program.Name),
            ("$degree", program.DegreeType), ("$discipline", program.DisciplineCode), ("$track", program.Track),
            ("$campus", program.Campus), ("$revision", program.Revision), ("$now", Now));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    });

    public AdmissionRound AddRound(AdmissionRound round) => Write(round, (connection, transaction) =>
    {
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO AdmissionRound(Id,ProgramId,CycleYear,EntryYear,Kind,RoundKey,Title,OfficialUrl,ApplicationUrl,Revision,CreatedAt,UpdatedAt)
            VALUES($id,$program,$cycle,$entry,$kind,$roundKey,$title,$official,$application,$revision,$now,$now)
            """, ("$id", round.Id), ("$program", round.ProgramId), ("$cycle", round.CycleYear), ("$entry", round.EntryYear),
            ("$kind", round.RoundKind.ToString()), ("$roundKey", round.RoundKey), ("$title", round.Title),
            ("$official", round.OfficialUrl), ("$application", round.ApplicationUrl), ("$revision", round.Revision), ("$now", Now));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    });

    public Appointment AddAppointment(Appointment appointment) => Write(appointment, (connection, transaction) =>
    {
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO Appointment(Id,ProfessorId,DepartmentId,Title,Role,StartDate,EndDate,IsPrimary,EvidenceClaimId,Revision,CreatedAt,UpdatedAt)
            VALUES($id,$professor,$department,$title,$role,$start,$end,$primary,$claim,$revision,$now,$now)
            """, ("$id", appointment.Id), ("$professor", appointment.ProfessorId), ("$department", appointment.DepartmentId),
            ("$title", appointment.Title), ("$role", appointment.Role), ("$start", appointment.StartDate?.ToString("yyyy-MM-dd")),
            ("$end", appointment.EndDate?.ToString("yyyy-MM-dd")), ("$primary", appointment.IsPrimary ? 1 : 0),
            ("$claim", appointment.EvidenceClaimId), ("$revision", appointment.Revision), ("$now", Now));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    });

    public ExternalIdentity AddExternalIdentity(ExternalIdentity identity) => Write(identity, (connection, transaction) =>
    {
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO ExternalIdentity(SourceKey,EntityKind,ExternalId,EntityId,CreatedAt)
            VALUES($source,$kind,$external,$entity,$now)
            """, ("$source", identity.SourceKey), ("$kind", identity.EntityKind), ("$external", identity.ExternalId),
            ("$entity", identity.EntityId), ("$now", Now));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    });

    public PageResult<School> ListSchools(PageRequest request)
    {
        var cursor = Decode<NameCursor>(request.After);
        var sql = """
            SELECT Id,CanonicalName,ShortName,OfficialCode,Campus,City,Revision
            FROM School
            WHERE ArchivedAt IS NULL
            """;
        if (cursor is not null) sql += " AND (CanonicalName COLLATE NOCASE > $name COLLATE NOCASE OR (CanonicalName COLLATE NOCASE = $name COLLATE NOCASE AND Id > $id))";
        sql += " ORDER BY CanonicalName COLLATE NOCASE,Id LIMIT $limit";
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, sql, ("$limit", request.EffectiveLimit + 1));
        if (cursor is not null) { command.Parameters.AddWithValue("$name", cursor.Name); command.Parameters.AddWithValue("$id", cursor.Id); }
        using var reader = command.ExecuteReader();
        var items = new List<School>();
        while (reader.Read()) items.Add(ReadSchool(reader));
        return Page(items, request.EffectiveLimit, item => new NameCursor(item.CanonicalName, item.Id));
    }

    public PageResult<Department> ListDepartments(string schoolId, PageRequest request)
    {
        var cursor = Decode<NameCursor>(request.After);
        var sql = """
            SELECT Id,SchoolId,CanonicalName,OfficialCode,ParentDepartmentId,Kind,Revision
            FROM Department WHERE SchoolId=$school AND ArchivedAt IS NULL
            """;
        if (cursor is not null) sql += " AND (CanonicalName COLLATE NOCASE > $name COLLATE NOCASE OR (CanonicalName COLLATE NOCASE = $name COLLATE NOCASE AND Id > $id))";
        sql += " ORDER BY CanonicalName COLLATE NOCASE,Id LIMIT $limit";
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, sql, ("$school", schoolId), ("$limit", request.EffectiveLimit + 1));
        if (cursor is not null) { command.Parameters.AddWithValue("$name", cursor.Name); command.Parameters.AddWithValue("$id", cursor.Id); }
        using var reader = command.ExecuteReader();
        var items = new List<Department>();
        while (reader.Read()) items.Add(ReadDepartment(reader));
        return Page(items, request.EffectiveLimit, item => new NameCursor(item.CanonicalName, item.Id));
    }

    public PageResult<AdmissionProgram> ListPrograms(string departmentId, PageRequest request)
    {
        var cursor = Decode<ProgramCursor>(request.After);
        var sql = """
            SELECT Id,DepartmentId,Name,DegreeType,DisciplineCode,Track,Campus,Revision
            FROM AdmissionProgram WHERE DepartmentId=$department AND ArchivedAt IS NULL
            """;
        if (cursor is not null) sql += " AND ((Name COLLATE NOCASE > $name COLLATE NOCASE) OR (Name COLLATE NOCASE = $name COLLATE NOCASE AND (DegreeType > $degree OR (DegreeType = $degree AND (COALESCE(Track,'') > $track OR (COALESCE(Track,'') = $track AND Id > $id))))))";
        sql += " ORDER BY Name COLLATE NOCASE,DegreeType,COALESCE(Track,''),Id LIMIT $limit";
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, sql, ("$department", departmentId), ("$limit", request.EffectiveLimit + 1));
        if (cursor is not null)
        {
            command.Parameters.AddWithValue("$name", cursor.Name);
            command.Parameters.AddWithValue("$degree", cursor.DegreeType);
            command.Parameters.AddWithValue("$track", cursor.Track);
            command.Parameters.AddWithValue("$id", cursor.Id);
        }
        using var reader = command.ExecuteReader();
        var items = new List<AdmissionProgram>();
        while (reader.Read()) items.Add(ReadProgram(reader));
        return Page(items, request.EffectiveLimit, item => new ProgramCursor(item.Name, item.DegreeType, item.Track ?? "", item.Id));
    }

    public PageResult<AdmissionRound> ListRounds(string programId, PageRequest request)
    {
        var cursor = Decode<RoundCursor>(request.After);
        var sql = """
            SELECT Id,ProgramId,CycleYear,EntryYear,Kind,RoundKey,Title,OfficialUrl,ApplicationUrl,Revision
            FROM AdmissionRound WHERE ProgramId=$program AND ArchivedAt IS NULL
            """;
        if (cursor is not null) sql += "\n" + """
            AND (
                (CASE WHEN CycleYear IS NULL THEN 1 ELSE 0 END) > $cycleNull
                OR ((CASE WHEN CycleYear IS NULL THEN 1 ELSE 0 END) = $cycleNull AND (
                    ($cycleNull = 0 AND CycleYear < $cycleYear)
                    OR (($cycleNull = 1 OR CycleYear = $cycleYear) AND (
                        (CASE WHEN EntryYear IS NULL THEN 1 ELSE 0 END) > $entryNull
                        OR ((CASE WHEN EntryYear IS NULL THEN 1 ELSE 0 END) = $entryNull AND (
                            ($entryNull = 0 AND EntryYear < $entryYear)
                            OR (($entryNull = 1 OR EntryYear = $entryYear) AND (
                                Kind > $kind OR (Kind = $kind AND (RoundKey > $roundKey OR (RoundKey = $roundKey AND Id > $id)))
                            ))
                        ))
                    ))
                ))
            )
            """;
        sql += " ORDER BY CASE WHEN CycleYear IS NULL THEN 1 ELSE 0 END,CycleYear DESC,CASE WHEN EntryYear IS NULL THEN 1 ELSE 0 END,EntryYear DESC,Kind,RoundKey,Id LIMIT $limit";
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, sql, ("$program", programId), ("$limit", request.EffectiveLimit + 1));
        if (cursor is not null)
        {
            command.Parameters.AddWithValue("$cycleNull", cursor.CycleYear is null ? 1 : 0);
            command.Parameters.AddWithValue("$cycleYear", cursor.CycleYear ?? 0);
            command.Parameters.AddWithValue("$entryNull", cursor.EntryYear is null ? 1 : 0);
            command.Parameters.AddWithValue("$entryYear", cursor.EntryYear ?? 0);
            command.Parameters.AddWithValue("$kind", cursor.Kind);
            command.Parameters.AddWithValue("$roundKey", cursor.RoundKey);
            command.Parameters.AddWithValue("$id", cursor.Id);
        }
        using var reader = command.ExecuteReader();
        var items = new List<AdmissionRound>();
        while (reader.Read()) items.Add(ReadRound(reader));
        return Page(items, request.EffectiveLimit, item => new RoundCursor(item.CycleYear, item.EntryYear, item.RoundKind.ToString(), item.RoundKey, item.Id));
    }

    public PageResult<Appointment> ListAppointments(string departmentId, PageRequest request)
    {
        var cursor = Decode<IdCursor>(request.After);
        var sql = """
            SELECT Id,ProfessorId,DepartmentId,Title,Role,StartDate,EndDate,IsPrimary,EvidenceClaimId,Revision
            FROM Appointment WHERE DepartmentId=$department AND ArchivedAt IS NULL
            """;
        if (cursor is not null) sql += " AND (ProfessorId > $professor OR (ProfessorId = $professor AND Id > $id))";
        sql += " ORDER BY ProfessorId,Id LIMIT $limit";
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, sql, ("$department", departmentId), ("$limit", request.EffectiveLimit + 1));
        if (cursor is not null) { command.Parameters.AddWithValue("$professor", cursor.ParentId); command.Parameters.AddWithValue("$id", cursor.Id); }
        using var reader = command.ExecuteReader();
        var items = new List<Appointment>();
        while (reader.Read()) items.Add(ReadAppointment(reader));
        return Page(items, request.EffectiveLimit, item => new IdCursor(item.ProfessorId, item.Id));
    }

    public PageResult<Professor> ListProfessors(PageRequest request)
    {
        var cursor = Decode<NameCursor>(request.After);
        var sql = "SELECT Id,Name,Institution,Homepage,Email,EvidenceJson FROM Professor";
        if (cursor is not null) sql += " WHERE (Name COLLATE NOCASE > $name COLLATE NOCASE OR (Name COLLATE NOCASE = $name COLLATE NOCASE AND Id > $id))";
        sql += " ORDER BY Name COLLATE NOCASE,Id LIMIT $limit";
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, sql, ("$limit", request.EffectiveLimit + 1));
        if (cursor is not null) { command.Parameters.AddWithValue("$name", cursor.Name); command.Parameters.AddWithValue("$id", cursor.Id); }
        using var reader = command.ExecuteReader();
        var items = new List<Professor>();
        while (reader.Read()) items.Add(ReadProfessor(reader));
        return Page(items, request.EffectiveLimit, item => new NameCursor(item.Name, item.Id));
    }

    private T Write<T>(T value, Action<SqliteConnection, SqliteTransaction> insert)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        insert(connection, transaction);
        using var revision = LocalDatabase.Command(connection, "UPDATE WorkspaceMeta SET DataRevision=DataRevision+1 WHERE Id=1");
        revision.Transaction = transaction;
        if (revision.ExecuteNonQuery() != 1) throw new InvalidOperationException("WORKSPACE_META_MISSING");
        transaction.Commit();
        return value;
    }

    private static PageResult<T> Page<T, TCursor>(List<T> items, int limit, Func<T, TCursor> cursor)
    {
        string? next = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            next = Encode(cursor(items[^1]));
        }
        return new PageResult<T>(items, next);
    }

    private static T? Decode<T>(string? value)
    {
        if (value is null) return default;
        try { return JsonSerializer.Deserialize<T>(Convert.FromBase64String(value)); }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
        { throw new ArgumentException("Invalid page cursor.", nameof(value), error); }
    }

    private static string Encode<T>(T value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value));

    private static School ReadSchool(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
        NullableString(reader, 3), NullableString(reader, 4), NullableString(reader, 5), reader.GetInt64(6));
    private static Department ReadDepartment(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(5),
        NullableString(reader, 3), NullableString(reader, 4), reader.GetInt64(6));
    private static AdmissionProgram ReadProgram(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        NullableString(reader, 4), NullableString(reader, 5), NullableString(reader, 6), reader.GetInt64(7));
    private static AdmissionRound ReadRound(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), NullableInt(reader, 2), NullableInt(reader, 3),
        Enum.Parse<AdmissionRoundKind>(reader.GetString(4)), reader.GetString(5), reader.GetString(6), NullableString(reader, 7), NullableString(reader, 8), reader.GetInt64(9));
    private static Appointment ReadAppointment(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
        NullableString(reader, 3), NullableString(reader, 4), Date(reader, 5), Date(reader, 6), reader.GetInt64(7) != 0, NullableString(reader, 8), reader.GetInt64(9));
    private static Professor ReadProfessor(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), NullableString(reader, 3),
        NullableString(reader, 4), JsonSerializer.Deserialize<Evidence[]>(reader.GetString(5), ResearchStore.Json)!);
    private static string? NullableString(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static int? NullableInt(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetInt32(index);
    private static DateOnly? Date(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : DateOnly.Parse(reader.GetString(index));

    private sealed record NameCursor(string Name, string Id);
    private sealed record ProgramCursor(string Name, string DegreeType, string Track, string Id);
    private sealed record RoundCursor(int? CycleYear, int? EntryYear, string Kind, string RoundKey, string Id);
    private sealed record IdCursor(string ParentId, string Id);
}
