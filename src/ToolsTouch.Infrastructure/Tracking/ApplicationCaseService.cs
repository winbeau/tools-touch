using Microsoft.Data.Sqlite;
using ToolsTouch.Application;
using ToolsTouch.Core;

namespace ToolsTouch.Infrastructure.Tracking;

public sealed class ApplicationCaseService(LocalDatabase database) : IApplicationCaseService
{
    private static string Now => DateTimeOffset.UtcNow.ToString("O");
    private static readonly HashSet<string> MaterialStates = ["Missing", "Ready", "Submitted", "NotApplicable"];
    private static readonly HashSet<string> ReminderStates = ["Pending", "Completed", "Dismissed"];

    public IReadOnlyList<ApplicationCaseSummary> List()
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            SELECT c.Id,c.DepartmentId,c.ProgramId,c.RoundId,c.ProfessorId,c.CycleYear,c.DegreeType,c.Stage,c.Priority,
                   c.ApplicationUrl,c.SubmittedAt,c.SubmissionReference,c.ReceiptArtifactId,c.NextStep,c.NextStepDueAt,
                   c.OwnerNote,c.Revision,c.CreatedAt,c.UpdatedAt,
                   s.CanonicalName,d.CanonicalName,p.Name,r.Title,prof.Name,
                   COALESCE((SELECT o.State FROM CaseOutreach co JOIN Outreach o ON o.Id=co.OutreachId
                             WHERE co.CaseId=c.Id ORDER BY o.CreatedAt DESC,o.Id DESC LIMIT 1),'NotContacted'),
                   COALESCE((SELECT COALESCE(t.ReplyState,'NoReply') FROM CaseOutreach co JOIN Outreach o ON o.Id=co.OutreachId
                             LEFT JOIN EmailThread t ON t.Account=o.SenderAccount AND t.ThreadId=o.ThreadId
                             WHERE co.CaseId=c.Id ORDER BY o.CreatedAt DESC,o.Id DESC LIMIT 1),'NoReply')
            FROM ApplicationCase c
            JOIN Department d ON d.Id=c.DepartmentId
            JOIN School s ON s.Id=d.SchoolId
            LEFT JOIN AdmissionProgram p ON p.Id=c.ProgramId
            LEFT JOIN AdmissionRound r ON r.Id=c.RoundId
            LEFT JOIN Professor prof ON prof.Id=c.ProfessorId
            ORDER BY c.CycleYear DESC,c.Priority DESC,s.CanonicalName COLLATE NOCASE,d.CanonicalName COLLATE NOCASE,c.Id
            """);
        using var reader = command.ExecuteReader();
        var result = new List<ApplicationCaseSummary>();
        while (reader.Read())
        {
            result.Add(new(ReadCase(reader), reader.GetString(19), reader.GetString(20), NullableString(reader, 21),
                NullableString(reader, 22), NullableString(reader, 23), reader.GetString(24), reader.GetString(25)));
        }
        return result;
    }

    public ApplicationCaseRecord Get(string caseId)
    {
        ApplicationStages.Required(caseId, nameof(caseId));
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, CaseSelect + " WHERE c.Id=$id", ("$id", caseId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("APPLICATION_CASE_NOT_FOUND");
        return ReadCase(reader);
    }

    public ApplicationCaseRecord Create(ApplicationCaseCreateRequest request)
    {
        var departmentId = ApplicationStages.Required(request.DepartmentId, nameof(request.DepartmentId));
        var degreeType = ApplicationStages.Required(request.DegreeType, nameof(request.DegreeType));
        var stage = ApplicationStages.Validate(request.Stage);
        ValidateYear(request.CycleYear);
        ValidatePriority(request.Priority);
        var applicationUrl = ApplicationStages.HttpUrl(request.ApplicationUrl, nameof(request.ApplicationUrl));
        var nextStep = ApplicationStages.Optional(request.NextStep);
        var ownerNote = ApplicationStages.Optional(request.OwnerNote);
        var nextStepDueAt = ApplicationStages.Date(request.NextStepDueAt);
        var id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : ApplicationStages.Required(request.Id, nameof(request.Id));
        var now = Now;

        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        ValidateOrganizationReferences(connection, transaction, departmentId, request.ProgramId, request.RoundId, request.ProfessorId);
        using (var insert = LocalDatabase.Command(connection, """
            INSERT INTO ApplicationCase(Id,DepartmentId,ProgramId,RoundId,ProfessorId,CycleYear,DegreeType,Stage,Priority,
                ApplicationUrl,NextStep,NextStepDueAt,OwnerNote,Revision,CreatedAt,UpdatedAt)
            VALUES($id,$department,$program,$round,$professor,$year,$degree,$stage,$priority,$url,$next,$due,$note,1,$now,$now)
            """, ("$id", id), ("$department", departmentId), ("$program", ApplicationStages.Optional(request.ProgramId)),
            ("$round", ApplicationStages.Optional(request.RoundId)), ("$professor", ApplicationStages.Optional(request.ProfessorId)),
            ("$year", request.CycleYear), ("$degree", degreeType), ("$stage", stage), ("$priority", request.Priority),
            ("$url", applicationUrl), ("$next", nextStep), ("$due", nextStepDueAt?.ToString("O")), ("$note", ownerNote), ("$now", now)))
        { insert.Transaction = transaction; insert.ExecuteNonQuery(); }
        InsertEvent(connection, transaction, id, "Created", null, stage, null, null, now, null);
        IncrementRevision(connection, transaction);
        transaction.Commit();
        return Get(id);
    }

    public ApplicationCaseRecord Update(ApplicationCaseUpdateRequest request)
    {
        ApplicationStages.Required(request.CaseId, nameof(request.CaseId));
        ValidatePriority(request.Priority);
        var applicationUrl = ApplicationStages.HttpUrl(request.ApplicationUrl, nameof(request.ApplicationUrl));
        var nextStep = ApplicationStages.Optional(request.NextStep);
        var ownerNote = ApplicationStages.Optional(request.OwnerNote);
        var due = ApplicationStages.Date(request.NextStepDueAt);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var update = LocalDatabase.Command(connection, """
            UPDATE ApplicationCase SET Priority=$priority,ApplicationUrl=$url,NextStep=$next,NextStepDueAt=$due,
                OwnerNote=$note,Revision=Revision+1,UpdatedAt=$now
            WHERE Id=$id AND Revision=$revision
            """, ("$priority", request.Priority), ("$url", applicationUrl), ("$next", nextStep), ("$due", due?.ToString("O")),
            ("$note", ownerNote), ("$now", Now), ("$id", request.CaseId), ("$revision", request.ExpectedRevision));
        update.Transaction = transaction;
        if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("APPLICATION_CASE_CHANGED");
        IncrementRevision(connection, transaction);
        transaction.Commit();
        return Get(request.CaseId);
    }

    public ApplicationCaseRecord ChangeStage(ApplicationStageChangeRequest request)
    {
        ApplicationStages.Required(request.CaseId, nameof(request.CaseId));
        var nextStage = ApplicationStages.Validate(request.NextStage);
        var occurredAt = request.OccurredAt.ToUniversalTime().ToString("O");
        var note = ApplicationStages.Optional(request.Note);
        var evidence = ApplicationStages.Optional(request.EvidenceArtifactId);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        string previousStage;
        using (var current = LocalDatabase.Command(connection, "SELECT Stage FROM ApplicationCase WHERE Id=$id AND Revision=$revision", ("$id", request.CaseId), ("$revision", request.ExpectedRevision)))
        {
            current.Transaction = transaction;
            previousStage = current.ExecuteScalar() as string ?? throw new InvalidOperationException("APPLICATION_CASE_CHANGED");
        }
        if (previousStage == nextStage) throw new InvalidOperationException("APPLICATION_STAGE_UNCHANGED");
        using (var update = LocalDatabase.Command(connection, "UPDATE ApplicationCase SET Stage=$stage,Revision=Revision+1,UpdatedAt=$now WHERE Id=$id AND Revision=$revision", ("$stage", nextStage), ("$now", Now), ("$id", request.CaseId), ("$revision", request.ExpectedRevision)))
        {
            update.Transaction = transaction;
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("APPLICATION_CASE_CHANGED");
        }
        InsertEvent(connection, transaction, request.CaseId, "StageChanged", previousStage, nextStage, null, evidence, occurredAt, note);
        IncrementRevision(connection, transaction);
        transaction.Commit();
        return Get(request.CaseId);
    }

    public ApplicationCaseRecord RecordSubmission(ApplicationSubmissionRequest request)
    {
        ApplicationStages.Required(request.CaseId, nameof(request.CaseId));
        var reference = ApplicationStages.Optional(request.SubmissionReference);
        var receipt = ApplicationStages.Optional(request.ReceiptArtifactId);
        var note = ApplicationStages.Optional(request.Note);
        var submittedAt = request.SubmittedAt.ToUniversalTime().ToString("O");
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using (var update = LocalDatabase.Command(connection, """
            UPDATE ApplicationCase SET SubmittedAt=$submitted,SubmissionReference=$reference,ReceiptArtifactId=$receipt,
                Revision=Revision+1,UpdatedAt=$now WHERE Id=$id AND Revision=$revision
            """, ("$submitted", submittedAt), ("$reference", reference), ("$receipt", receipt), ("$now", Now),
            ("$id", request.CaseId), ("$revision", request.ExpectedRevision)))
        {
            update.Transaction = transaction;
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("APPLICATION_CASE_CHANGED");
        }
        InsertEvent(connection, transaction, request.CaseId, "SubmissionRecorded", null, null, null, receipt, submittedAt, note);
        IncrementRevision(connection, transaction);
        transaction.Commit();
        return Get(request.CaseId);
    }

    public IReadOnlyList<ApplicationEventRecord> Events(string caseId)
    {
        ApplicationStages.Required(caseId, nameof(caseId));
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            SELECT Id,CaseId,Type,PreviousStage,NextStage,RelatedOutreachId,EvidenceArtifactId,OccurredAt,RecordedAt,Note
            FROM ApplicationEvent WHERE CaseId=$case ORDER BY RecordedAt DESC,Id DESC
            """, ("$case", caseId));
        using var reader = command.ExecuteReader();
        var result = new List<ApplicationEventRecord>();
        while (reader.Read()) result.Add(ReadEvent(reader));
        return result;
    }

    public ApplicationMaterialRecord AddMaterial(ApplicationMaterialCreateRequest request)
    {
        var caseId = ApplicationStages.Required(request.CaseId, nameof(request.CaseId));
        var name = ApplicationStages.Required(request.Name, nameof(request.Name));
        var state = ValidateMaterialState(request.State);
        var artifact = ApplicationStages.Optional(request.ArtifactId);
        var note = ApplicationStages.Optional(request.Note);
        var id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : ApplicationStages.Required(request.Id, nameof(request.Id));
        var now = Now;
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using (var insert = LocalDatabase.Command(connection, """
            INSERT INTO ApplicationMaterial(Id,CaseId,Name,IsRequired,State,ArtifactId,Note,Revision,CreatedAt,UpdatedAt)
            VALUES($id,$case,$name,$required,$state,$artifact,$note,1,$now,$now)
            """, ("$id", id), ("$case", caseId), ("$name", name), ("$required", request.IsRequired ? 1 : 0),
            ("$state", state), ("$artifact", artifact), ("$note", note), ("$now", now)))
        { insert.Transaction = transaction; insert.ExecuteNonQuery(); }
        IncrementRevision(connection, transaction);
        transaction.Commit();
        return Materials(caseId).Single(item => item.Id == id);
    }

    public ApplicationMaterialRecord UpdateMaterial(ApplicationMaterialUpdateRequest request)
    {
        var state = ValidateMaterialState(request.State);
        var artifact = ApplicationStages.Optional(request.ArtifactId);
        var note = ApplicationStages.Optional(request.Note);
        string caseId;
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using (var lookup = LocalDatabase.Command(connection, "SELECT CaseId FROM ApplicationMaterial WHERE Id=$id", ("$id", request.MaterialId)))
        {
            lookup.Transaction = transaction;
            caseId = lookup.ExecuteScalar() as string ?? throw new KeyNotFoundException("APPLICATION_MATERIAL_NOT_FOUND");
        }
        using (var update = LocalDatabase.Command(connection, """
            UPDATE ApplicationMaterial SET IsRequired=$required,State=$state,ArtifactId=$artifact,Note=$note,
                Revision=Revision+1,UpdatedAt=$now WHERE Id=$id AND Revision=$revision
            """, ("$required", request.IsRequired ? 1 : 0), ("$state", state), ("$artifact", artifact), ("$note", note),
            ("$now", Now), ("$id", request.MaterialId), ("$revision", request.ExpectedRevision)))
        {
            update.Transaction = transaction;
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("APPLICATION_MATERIAL_CHANGED");
        }
        IncrementRevision(connection, transaction);
        transaction.Commit();
        return Materials(caseId).Single(item => item.Id == request.MaterialId);
    }

    public IReadOnlyList<ApplicationMaterialRecord> Materials(string caseId)
    {
        ApplicationStages.Required(caseId, nameof(caseId));
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            SELECT Id,CaseId,Name,IsRequired,State,ArtifactId,Note,Revision,CreatedAt,UpdatedAt
            FROM ApplicationMaterial WHERE CaseId=$case ORDER BY Name COLLATE NOCASE,Id
            """, ("$case", caseId));
        using var reader = command.ExecuteReader();
        var result = new List<ApplicationMaterialRecord>();
        while (reader.Read()) result.Add(ReadMaterial(reader));
        return result;
    }

    public ApplicationReminderRecord AddReminder(ApplicationReminderCreateRequest request)
    {
        var caseId = ApplicationStages.Optional(request.CaseId);
        var roundId = ApplicationStages.Optional(request.RoundId);
        if (caseId is null && roundId is null) throw new ArgumentException("A reminder must be linked to a case or round.", nameof(request));
        var kind = ApplicationStages.Required(request.Kind, nameof(request.Kind));
        var basis = ApplicationStages.Required(request.Basis, nameof(request.Basis));
        var state = ValidateReminderState(request.State);
        var id = string.IsNullOrWhiteSpace(request.Id) ? Guid.NewGuid().ToString("N") : ApplicationStages.Required(request.Id, nameof(request.Id));
        var now = Now;
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using (var insert = LocalDatabase.Command(connection, """
            INSERT INTO Reminder(Id,CaseId,RoundId,Kind,DueAt,Basis,State,CreatedAt,UpdatedAt)
            VALUES($id,$case,$round,$kind,$due,$basis,$state,$now,$now)
            """, ("$id", id), ("$case", caseId), ("$round", roundId), ("$kind", kind), ("$due", request.DueAt.ToUniversalTime().ToString("O")),
            ("$basis", basis), ("$state", state), ("$now", now)))
        { insert.Transaction = transaction; insert.ExecuteNonQuery(); }
        IncrementRevision(connection, transaction);
        transaction.Commit();
        return Reminders(caseId).Single(item => item.Id == id);
    }

    public ApplicationReminderRecord UpdateReminder(ApplicationReminderUpdateRequest request)
    {
        var state = ValidateReminderState(request.State);
        var kind = ApplicationStages.Required(request.Basis, nameof(request.Basis));
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var update = LocalDatabase.Command(connection, "UPDATE Reminder SET State=$state,DueAt=$due,Basis=$basis,UpdatedAt=$now WHERE Id=$id", ("$state", state), ("$due", request.DueAt.ToUniversalTime().ToString("O")), ("$basis", kind), ("$now", Now), ("$id", request.ReminderId));
        update.Transaction = transaction;
        if (update.ExecuteNonQuery() != 1) throw new KeyNotFoundException("APPLICATION_REMINDER_NOT_FOUND");
        IncrementRevision(connection, transaction);
        transaction.Commit();
        return Reminders().Single(item => item.Id == request.ReminderId);
    }

    public IReadOnlyList<ApplicationReminderRecord> Reminders(string? caseId = null)
    {
        if (caseId is not null) ApplicationStages.Required(caseId, nameof(caseId));
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            SELECT Id,CaseId,RoundId,Kind,DueAt,Basis,State,CreatedAt,UpdatedAt
            FROM Reminder WHERE $case IS NULL OR CaseId=$case ORDER BY DueAt,Id
            """, ("$case", caseId));
        using var reader = command.ExecuteReader();
        var result = new List<ApplicationReminderRecord>();
        while (reader.Read()) result.Add(ReadReminder(reader));
        return result;
    }

    public void LinkOutreach(string caseId, string outreachId)
    {
        ApplicationStages.Required(caseId, nameof(caseId));
        ApplicationStages.Required(outreachId, nameof(outreachId));
        var now = Now;
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using (var link = LocalDatabase.Command(connection, "INSERT INTO CaseOutreach(CaseId,OutreachId,CreatedAt) VALUES($case,$outreach,$now)", ("$case", caseId), ("$outreach", outreachId), ("$now", now)))
        { link.Transaction = transaction; link.ExecuteNonQuery(); }
        InsertEvent(connection, transaction, caseId, "OutreachLinked", null, null, outreachId, null, now, null);
        IncrementRevision(connection, transaction);
        transaction.Commit();
    }

    public IReadOnlyList<string> OutreachIds(string caseId)
    {
        ApplicationStages.Required(caseId, nameof(caseId));
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "SELECT OutreachId FROM CaseOutreach WHERE CaseId=$case ORDER BY CreatedAt,OutreachId", ("$case", caseId));
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    private const string CaseSelect = """
        SELECT c.Id,c.DepartmentId,c.ProgramId,c.RoundId,c.ProfessorId,c.CycleYear,c.DegreeType,c.Stage,c.Priority,
               c.ApplicationUrl,c.SubmittedAt,c.SubmissionReference,c.ReceiptArtifactId,c.NextStep,c.NextStepDueAt,
               c.OwnerNote,c.Revision,c.CreatedAt,c.UpdatedAt
        FROM ApplicationCase c
        """;

    private static ApplicationCaseRecord ReadCase(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), NullableString(reader, 2), NullableString(reader, 3), NullableString(reader, 4),
        reader.GetInt32(5), reader.GetString(6), reader.GetString(7), reader.GetInt32(8), NullableString(reader, 9),
        NullableDate(reader, 10), NullableString(reader, 11), NullableString(reader, 12), NullableString(reader, 13),
        NullableDate(reader, 14), NullableString(reader, 15), reader.GetInt32(16), Date(reader, 17), Date(reader, 18));

    private static ApplicationEventRecord ReadEvent(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
        NullableString(reader, 3), NullableString(reader, 4), NullableString(reader, 5), NullableString(reader, 6), Date(reader, 7), Date(reader, 8), NullableString(reader, 9));

    private static ApplicationMaterialRecord ReadMaterial(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.GetInt64(3) != 0, reader.GetString(4), NullableString(reader, 5), NullableString(reader, 6), reader.GetInt32(7), Date(reader, 8), Date(reader, 9));

    private static ApplicationReminderRecord ReadReminder(SqliteDataReader reader) => new(reader.GetString(0), NullableString(reader, 1), NullableString(reader, 2),
        reader.GetString(3), Date(reader, 4), reader.GetString(5), reader.GetString(6), Date(reader, 7), Date(reader, 8));

    private static DateTimeOffset Date(SqliteDataReader reader, int index) => DateTimeOffset.Parse(reader.GetString(index));
    private static DateTimeOffset? NullableDate(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : Date(reader, index);
    private static string? NullableString(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);

    private static void ValidateYear(int year)
    {
        if (year is < 1900 or > 2200) throw new ArgumentOutOfRangeException(nameof(year));
    }

    private static void ValidatePriority(int priority)
    {
        if (priority is < 0 or > 5) throw new ArgumentOutOfRangeException(nameof(priority));
    }

    private static string ValidateMaterialState(string value)
    {
        value = ApplicationStages.Required(value, nameof(value));
        return MaterialStates.Contains(value) ? value : throw new ArgumentException("Unknown material state.", nameof(value));
    }

    private static string ValidateReminderState(string value)
    {
        value = ApplicationStages.Required(value, nameof(value));
        return ReminderStates.Contains(value) ? value : throw new ArgumentException("Unknown reminder state.", nameof(value));
    }

    private static void ValidateOrganizationReferences(SqliteConnection connection, SqliteTransaction transaction,
        string departmentId, string? programId, string? roundId, string? professorId)
    {
        using (var department = LocalDatabase.Command(connection, "SELECT COUNT(*) FROM Department WHERE Id=$id", ("$id", departmentId)))
        {
            department.Transaction = transaction;
            if (Convert.ToInt32(department.ExecuteScalar()) != 1) throw new InvalidOperationException("APPLICATION_DEPARTMENT_NOT_FOUND");
        }
        if (programId is not null)
        {
            using var program = LocalDatabase.Command(connection, "SELECT DepartmentId FROM AdmissionProgram WHERE Id=$id", ("$id", programId));
            program.Transaction = transaction;
            if (program.ExecuteScalar() is not string owningDepartment || owningDepartment != departmentId)
                throw new InvalidOperationException("APPLICATION_PROGRAM_SCOPE_MISMATCH");
        }
        if (roundId is not null)
        {
            using var round = LocalDatabase.Command(connection, """
                SELECT p.DepartmentId FROM AdmissionRound r JOIN AdmissionProgram p ON p.Id=r.ProgramId WHERE r.Id=$id
                """, ("$id", roundId));
            round.Transaction = transaction;
            if (round.ExecuteScalar() is not string owningDepartment || owningDepartment != departmentId)
                throw new InvalidOperationException("APPLICATION_ROUND_SCOPE_MISMATCH");
        }
        if (professorId is not null)
        {
            using var professor = LocalDatabase.Command(connection, "SELECT COUNT(*) FROM Professor WHERE Id=$id", ("$id", professorId));
            professor.Transaction = transaction;
            if (Convert.ToInt32(professor.ExecuteScalar()) != 1) throw new InvalidOperationException("APPLICATION_PROFESSOR_NOT_FOUND");
        }
    }

    private static void InsertEvent(SqliteConnection connection, SqliteTransaction transaction, string caseId, string type,
        string? previousStage, string? nextStage, string? relatedOutreachId, string? evidenceArtifactId, string occurredAt, string? note)
    {
        using var eventInsert = LocalDatabase.Command(connection, """
            INSERT INTO ApplicationEvent(Id,CaseId,Type,PreviousStage,NextStage,RelatedOutreachId,EvidenceArtifactId,OccurredAt,RecordedAt,Note)
            VALUES($id,$case,$type,$previous,$next,$outreach,$evidence,$occurred,$recorded,$note)
            """, ("$id", Guid.NewGuid().ToString("N")), ("$case", caseId), ("$type", type), ("$previous", previousStage),
            ("$next", nextStage), ("$outreach", relatedOutreachId), ("$evidence", evidenceArtifactId), ("$occurred", occurredAt),
            ("$recorded", Now), ("$note", note));
        eventInsert.Transaction = transaction;
        eventInsert.ExecuteNonQuery();
    }

    private static void IncrementRevision(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var revision = LocalDatabase.Command(connection, "UPDATE WorkspaceMeta SET DataRevision=DataRevision+1 WHERE Id=1");
        revision.Transaction = transaction;
        if (revision.ExecuteNonQuery() != 1) throw new InvalidOperationException("WORKSPACE_META_MISSING");
    }
}
