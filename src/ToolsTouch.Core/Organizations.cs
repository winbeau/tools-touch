namespace ToolsTouch.Core;

public sealed record School
{
    public School(string id, string canonicalName, string shortName, string? officialCode = null,
        string? campus = null, string? city = null, long revision = 1)
    {
        Id = Required(id, nameof(id));
        CanonicalName = Required(canonicalName, nameof(canonicalName));
        ShortName = Required(shortName, nameof(shortName));
        OfficialCode = Optional(officialCode);
        Campus = Optional(campus);
        City = Optional(city);
        Revision = revision > 0 ? revision : throw new ArgumentOutOfRangeException(nameof(revision));
    }

    public string Id { get; }
    public string CanonicalName { get; }
    public string ShortName { get; }
    public string? OfficialCode { get; }
    public string? Campus { get; }
    public string? City { get; }
    public long Revision { get; }

    public static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("A value is required.", name) : value.Trim();
    public static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record Department
{
    public Department(string id, string schoolId, string canonicalName, string kind,
        string? officialCode = null, string? parentDepartmentId = null, long revision = 1)
    {
        Id = School.Required(id, nameof(id));
        SchoolId = School.Required(schoolId, nameof(schoolId));
        CanonicalName = School.Required(canonicalName, nameof(canonicalName));
        Kind = School.Required(kind, nameof(kind));
        OfficialCode = School.Optional(officialCode);
        ParentDepartmentId = School.Optional(parentDepartmentId);
        Revision = revision > 0 ? revision : throw new ArgumentOutOfRangeException(nameof(revision));
    }

    public string Id { get; }
    public string SchoolId { get; }
    public string CanonicalName { get; }
    public string? OfficialCode { get; }
    public string? ParentDepartmentId { get; }
    public string Kind { get; }
    public long Revision { get; }
}

public sealed record AdmissionProgram
{
    public AdmissionProgram(string id, string departmentId, string name, string degreeType,
        string? disciplineCode = null, string? track = null, string? campus = null, long revision = 1)
    {
        Id = School.Required(id, nameof(id));
        DepartmentId = School.Required(departmentId, nameof(departmentId));
        Name = School.Required(name, nameof(name));
        DegreeType = School.Required(degreeType, nameof(degreeType));
        DisciplineCode = School.Optional(disciplineCode);
        Track = School.Optional(track);
        Campus = School.Optional(campus);
        Revision = revision > 0 ? revision : throw new ArgumentOutOfRangeException(nameof(revision));
    }

    public string Id { get; }
    public string DepartmentId { get; }
    public string Name { get; }
    public string DegreeType { get; }
    public string? DisciplineCode { get; }
    public string? Track { get; }
    public string? Campus { get; }
    public long Revision { get; }
}

public enum AdmissionRoundKind
{
    SummerCamp,
    PreRecommendation,
    Other
}

public sealed record AdmissionRound
{
    public AdmissionRound(string id, string programId, int? cycleYear, int? entryYear,
        AdmissionRoundKind kind, string roundKey, string title, string? officialUrl = null,
        string? applicationUrl = null, long revision = 1)
    {
        Id = School.Required(id, nameof(id));
        ProgramId = School.Required(programId, nameof(programId));
        CycleYear = ValidYear(cycleYear, nameof(cycleYear));
        EntryYear = ValidYear(entryYear, nameof(entryYear));
        RoundKind = kind;
        RoundKey = School.Required(roundKey, nameof(roundKey));
        Title = School.Required(title, nameof(title));
        OfficialUrl = Url(officialUrl, nameof(officialUrl));
        ApplicationUrl = Url(applicationUrl, nameof(applicationUrl));
        Revision = revision > 0 ? revision : throw new ArgumentOutOfRangeException(nameof(revision));
    }

    public string Id { get; }
    public string ProgramId { get; }
    public int? CycleYear { get; }
    public int? EntryYear { get; }
    public AdmissionRoundKind RoundKind { get; }
    public string RoundKey { get; }
    public string Title { get; }
    public string? OfficialUrl { get; }
    public string? ApplicationUrl { get; }
    public long Revision { get; }

    private static int? ValidYear(int? value, string name) => value is null or >= 1900 and <= 2200
        ? value : throw new ArgumentOutOfRangeException(name);

    private static string? Url(string? value, string name)
    {
        value = School.Optional(value);
        if (value is not null && (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            throw new ArgumentException("Only an HTTP(S) URL is allowed.", name);
        return value;
    }
}

public sealed record Appointment
{
    public Appointment(string id, string professorId, string departmentId, string? title = null,
        string? role = null, DateOnly? startDate = null, DateOnly? endDate = null,
        bool isPrimary = false, string? evidenceClaimId = null, long revision = 1)
    {
        if (startDate is not null && endDate is not null && endDate < startDate)
            throw new ArgumentException("End date must not precede start date.", nameof(endDate));
        Id = School.Required(id, nameof(id));
        ProfessorId = School.Required(professorId, nameof(professorId));
        DepartmentId = School.Required(departmentId, nameof(departmentId));
        Title = School.Optional(title);
        Role = School.Optional(role);
        StartDate = startDate;
        EndDate = endDate;
        IsPrimary = isPrimary;
        EvidenceClaimId = School.Optional(evidenceClaimId);
        Revision = revision > 0 ? revision : throw new ArgumentOutOfRangeException(nameof(revision));
    }

    public string Id { get; }
    public string ProfessorId { get; }
    public string DepartmentId { get; }
    public string? Title { get; }
    public string? Role { get; }
    public DateOnly? StartDate { get; }
    public DateOnly? EndDate { get; }
    public bool IsPrimary { get; }
    public string? EvidenceClaimId { get; }
    public long Revision { get; }
}

public sealed record WorkspaceMetadata(string WorkspaceId, long DataRevision, int SchemaVersion);

public sealed record ExternalIdentity
{
    public ExternalIdentity(string sourceKey, string entityKind, string externalId, string entityId)
    {
        SourceKey = School.Required(sourceKey, nameof(sourceKey));
        EntityKind = School.Required(entityKind, nameof(entityKind));
        ExternalId = School.Required(externalId, nameof(externalId));
        EntityId = School.Required(entityId, nameof(entityId));
    }

    public string SourceKey { get; }
    public string EntityKind { get; }
    public string ExternalId { get; }
    public string EntityId { get; }
}
