using ToolsTouch.Core;

namespace ToolsTouch.Application;

public sealed record PageRequest(int Limit = 50, string? After = null)
{
    public int EffectiveLimit => Math.Clamp(Limit, 1, 200);
}

public sealed record PageResult<T>(IReadOnlyList<T> Items, string? NextCursor);

public interface IOrganizationCatalog
{
    PageResult<School> ListSchools(PageRequest request);
    PageResult<Department> ListDepartments(string schoolId, PageRequest request);
    PageResult<AdmissionProgram> ListPrograms(string departmentId, PageRequest request);
    PageResult<AdmissionRound> ListRounds(string programId, PageRequest request);
    PageResult<Appointment> ListAppointments(string departmentId, PageRequest request);
    PageResult<Professor> ListProfessors(PageRequest request);
}

public interface IWorkspaceMetadata
{
    WorkspaceMetadata Get();
}

public interface IOrganizationWriter
{
    School AddSchool(School school);
    Department AddDepartment(Department department);
    AdmissionProgram AddProgram(AdmissionProgram program);
    AdmissionRound AddRound(AdmissionRound round);
    Appointment AddAppointment(Appointment appointment);
    ExternalIdentity AddExternalIdentity(ExternalIdentity identity);
}

public sealed class OrganizationService(IOrganizationCatalog catalog, IOrganizationWriter writer)
{
    public PageResult<School> ListSchools(PageRequest request) => catalog.ListSchools(request);
    public PageResult<Department> ListDepartments(string schoolId, PageRequest request) => catalog.ListDepartments(schoolId, request);
    public PageResult<AdmissionProgram> ListPrograms(string departmentId, PageRequest request) => catalog.ListPrograms(departmentId, request);
    public PageResult<AdmissionRound> ListRounds(string programId, PageRequest request) => catalog.ListRounds(programId, request);
    public PageResult<Appointment> ListAppointments(string departmentId, PageRequest request) => catalog.ListAppointments(departmentId, request);
    public School AddSchool(School school) => writer.AddSchool(school);
    public Department AddDepartment(Department department) => writer.AddDepartment(department);
    public AdmissionProgram AddProgram(AdmissionProgram program) => writer.AddProgram(program);
    public AdmissionRound AddRound(AdmissionRound round) => writer.AddRound(round);
    public Appointment AddAppointment(Appointment appointment) => writer.AddAppointment(appointment);
    public ExternalIdentity AddExternalIdentity(ExternalIdentity identity) => writer.AddExternalIdentity(identity);
}
