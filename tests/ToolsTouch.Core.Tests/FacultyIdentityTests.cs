using System.Text;
using Microsoft.Data.Sqlite;
using ToolsTouch.Application;
using ToolsTouch.Core;
using ToolsTouch.Infrastructure.Collection;

static class FacultyIdentityTests
{
    public static async Task RunAsync(LocalDatabase database, string directory)
    {
        var organizations = new OrganizationRepository(database);
        var school = organizations.AddSchool(new School("identity-school", "Identity University", "Identity U"));
        var department = organizations.AddDepartment(new Department("identity-department", school.Id, "Computer Science", "Department"));
        var artifacts = new ArtifactStore(database.ArtifactDirectory);
        var sources = new SourceRepository(database, artifacts);
        var crawler = new FacultyDirectoryCrawler(database, artifacts, sources);
        var importer = new FacultyIdentityImportService(database);
        var ada = new FacultyDirectoryCandidate("source-ada", "Ada Lovelace", "https://faculty.example.edu/ada", "Professor",
            "Ada Lovelace Professor", "https://faculty.example.edu/directory", PublicEmail: "ada@example.edu", PublicEmailRaw: "mailto:ada@example.edu");
        var noEmail = new FacultyDirectoryCandidate("source-no-email", "No Email", null, "Lecturer",
            "No Email Lecturer", "https://faculty.example.edu/directory");
        var ambiguous = new FacultyDirectoryCandidate("source-ambiguous", "Same Name", null, "Professor",
            "Same Name Professor", "https://faculty.example.edu/directory");
        InsertProfessor(database, "ambiguous-a", "Same Name", school.CanonicalName);
        InsertProfessor(database, "ambiguous-b", "Same Name", school.CanonicalName);

        var scope = new FacultyDirectoryScope(school.Id, department.Id, "https://faculty.example.edu/directory", 3);
        var adapter = new IdentityAdapter([ada, noEmail, ambiguous], "v1");
        var batch = await crawler.CrawlAsync(adapter, scope);
        var first = await importer.ImportAsync(batch.BatchId);
        Check(first.ImportedProfessorCount == 2 && first.AmbiguousCount == 1 && first.AppointmentCount == 2,
            "identity import creates unambiguous professors and preserves ambiguous candidates for review");
        Check(ScalarString(database, "SELECT Email FROM Professor WHERE Id=(SELECT EntityId FROM ExternalIdentity WHERE SourceKey='official-faculty-directory' AND ExternalId='source-ada')") == "ada@example.edu" &&
            ScalarString(database, "SELECT Homepage FROM Professor WHERE Id=(SELECT EntityId FROM ExternalIdentity WHERE SourceKey='official-faculty-directory' AND ExternalId='source-no-email')") is null,
            "explicit public email is retained while a no-email directory entry remains a faculty record");
        Check(Count(database, "SELECT COUNT(*) FROM FacultyIdentityResolution WHERE State='Pending'") == 1 &&
            Count(database, "SELECT COUNT(*) FROM FacultyIdentityResolution WHERE State='Pending' AND CandidateIdsJson LIKE '%ambiguous-a%' AND CandidateIdsJson LIKE '%ambiguous-b%'") == 1,
            "same-name candidates are queued with both possible professor IDs");
        var revision = new OrganizationRepository(database).Get().DataRevision;

        var repeated = await importer.ImportAsync(batch.BatchId);
        Check(repeated.ImportedProfessorCount == 0 && repeated.UpdatedProfessorCount == 0 && repeated.UnchangedProfessorCount == 2 &&
            repeated.DatasetRevision == revision && Count(database, "SELECT COUNT(*) FROM Appointment WHERE DepartmentId='identity-department'") == 2,
            $"repeating one crawl batch is idempotent and does not duplicate appointments or advance the revision: imported={repeated.ImportedProfessorCount}, updated={repeated.UpdatedProfessorCount}, unchanged={repeated.UnchangedProfessorCount}, revision={repeated.DatasetRevision}/{revision}, appointments={Count(database, "SELECT COUNT(*) FROM Appointment WHERE DepartmentId='identity-department'")}");

        var secondDepartment = organizations.AddDepartment(new Department("identity-department-2", school.Id, "Data Science", "Department"));
        var secondScope = scope with { DepartmentId = secondDepartment.Id, DeclaredFacultyCount = 1 };
        var samePersonBatch = await crawler.CrawlAsync(new IdentityAdapter([ada], "v2"), secondScope);
        var samePerson = await importer.ImportAsync(samePersonBatch.BatchId);
        Check(samePerson.ImportedProfessorCount == 0 && samePerson.AppointmentCount == 1 &&
            Count(database, "SELECT COUNT(*) FROM Appointment WHERE ProfessorId=(SELECT EntityId FROM ExternalIdentity WHERE SourceKey='official-faculty-directory' AND ExternalId='source-ada')") == 2,
            "one stable source identity can retain appointments in multiple departments");

        var migrated = ada with
        {
            HomepageUrl = "https://faculty.example.edu/ada-new",
            PublicEmail = "ada-new@example.edu",
            PublicEmailRaw = "ada-new@example.edu"
        };
        var migratedBatch = await crawler.CrawlAsync(new IdentityAdapter([migrated], "v3"), scope);
        await importer.ImportAsync(migratedBatch.BatchId);
        Check(ScalarString(database, "SELECT Homepage FROM Professor WHERE Id=(SELECT EntityId FROM ExternalIdentity WHERE SourceKey='official-faculty-directory' AND ExternalId='source-ada')") == "https://faculty.example.edu/ada-new" &&
            ScalarString(database, "SELECT Email FROM Professor WHERE Id=(SELECT EntityId FROM ExternalIdentity WHERE SourceKey='official-faculty-directory' AND ExternalId='source-ada')") == "ada-new@example.edu" &&
            Count(database, "SELECT COUNT(*) FROM SourceSnapshot WHERE SourceKey='official-faculty-directory' AND ExternalRecordId='https://faculty.example.edu/directory'") >= 3,
            "a stable identity can follow a homepage migration while immutable old page snapshots remain");
        Check(Count(database, "SELECT COUNT(*) FROM EvidenceClaim WHERE ClaimType='public_email'") >= 2 &&
            Count(database, "SELECT COUNT(*) FROM EvidenceClaim WHERE ClaimType='faculty_directory_entry'") >= 3,
            "directory appointment and public email claims retain source evidence");
        Console.WriteLine("PASS: faculty identity precedence, ambiguity queue, multi-department appointments, homepage migration and public email evidence");
    }

    private sealed class IdentityAdapter(FacultyDirectoryCandidate[] candidates, string contentVersion) : IFacultyDirectorySourceAdapter
    {
        public SourceAdapterDescriptor Descriptor { get; } = new("official-faculty-directory", "fixture-identity-v1", "Identity fixture", ["directory"]);
        public IReadOnlyList<FacultyCrawlTarget> DiscoverScope(FacultyDirectoryScope scope) => [new(scope.NormalizedSeedUrl)];
        public Task<FacultySourcePage> FetchAsync(FacultyCrawlTarget target, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FacultySourcePage(target.NormalizedUrl, target.NormalizedUrl, "text/html",
                Encoding.UTF8.GetBytes("<html>identity-" + contentVersion + "</html>"), DateTimeOffset.UtcNow));
        public FacultyDirectoryPage Parse(FacultyDirectoryScope scope, FacultySourcePage page) =>
            new(page.RequestedUrl, page.FinalUrl, candidates, [], candidates.Length);
        public FacultyDirectoryCandidate Normalize(FacultyDirectoryCandidate candidate) => candidate with { Name = candidate.Name.Trim() };
        public Task<SourceAdapterHealth> HealthAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SourceAdapterHealth(true, "Ready"));
    }

    private static void InsertProfessor(LocalDatabase database, string id, string name, string institution)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO Professor(Id,Name,Institution,Homepage,Email,EvidenceJson,UpdatedAt)
            VALUES($id,$name,$institution,NULL,NULL,'[]',$now)
            """, ("$id", id), ("$name", name), ("$institution", institution), ("$now", DateTimeOffset.UtcNow.ToString("O")));
        command.ExecuteNonQuery();
    }

    private static int Count(LocalDatabase database, string sql)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, sql);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string? ScalarString(LocalDatabase database, string sql)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, sql);
        var value = command.ExecuteScalar();
        return value is DBNull or null ? null : Convert.ToString(value);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
