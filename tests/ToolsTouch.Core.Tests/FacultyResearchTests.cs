using System.Text;
using Microsoft.Data.Sqlite;
using ToolsTouch.Application;
using ToolsTouch.Core;
using ToolsTouch.Infrastructure.Research;

static class FacultyResearchTests
{
    public static Task RunAsync(LocalDatabase database, string directory)
    {
        var professorId = "p04c-researcher";
        InsertProfessor(database, professorId, "Test Researcher", "Research University", null, null);
        var artifacts = new ArtifactStore(database.ArtifactDirectory);
        var sources = new SourceRepository(database, artifacts);
        var service = new FacultyResearchService(database, artifacts, sources);
        var homepage = new HomepageEvidenceInput(professorId, "https://faculty.example.edu/test", "Test homepage",
            "Research Interests: language models and retrieval\n2026 招生：硕士、博士\n联系 mailto:test@example.edu", DateTimeOffset.Parse("2026-09-05T00:00:00Z"));
        var firstHomepage = service.ImportHomepageEvidence(homepage);
        Check(firstHomepage.ClaimCount == 2 && firstHomepage.PublicEmail == "test@example.edu" &&
            Count(database, "SELECT COUNT(*) FROM EvidenceClaim WHERE SubjectId=$id AND ClaimType IN ('research_direction','recruitment_statement')", ("$id", professorId)) == 2,
            "homepage parser stores research and year-scoped recruitment claims with explicit email");
        var revision = new OrganizationRepository(database).Get().DataRevision;
        var repeatedHomepage = service.ImportHomepageEvidence(homepage);
        Check(repeatedHomepage.ClaimCount == 0 && repeatedHomepage.SnapshotId == firstHomepage.SnapshotId &&
            repeatedHomepage.DatasetRevision == revision,
            "repeating an unchanged homepage is idempotent");

        var paper = new Paper("p04c-paper", "doi:10.1000/test", "A Test Paper", ["Test Researcher", "Other Author"], 2026,
            "An abstract", "https://doi.org/10.1000/test", null, "MetadataOnly");
        var attribution = service.ImportPaper(paper, professorId);
        Check(attribution.State == PaperAttributionState.Attributed && attribution.ProfessorId == professorId &&
            Count(database, "SELECT COUNT(*) FROM ProfessorPaper WHERE ProfessorId=$prof AND PaperId=$paper", ("$prof", professorId), ("$paper", attribution.PaperId)) == 1,
            "paper is attributed only when exactly one normalized author matches");
        var paperRevision = attribution.DatasetRevision;
        var repeatedPaper = service.ImportPaper(paper, professorId);
        Check(repeatedPaper.State == PaperAttributionState.Attributed && repeatedPaper.DatasetRevision == paperRevision &&
            Count(database, "SELECT COUNT(*) FROM ProfessorPaper WHERE ProfessorId=$prof AND PaperId=$paper", ("$prof", professorId), ("$paper", attribution.PaperId)) == 1,
            "repeating paper metadata does not duplicate attribution or change revision");

        var ambiguous = service.ImportPaper(paper with { Id = "p04c-paper-ambiguous", ExternalId = "doi:10.1000/ambiguous", Authors = ["Test Researcher", "Test Researcher"] }, professorId);
        Check(ambiguous.State == PaperAttributionState.NeedsVerification && ambiguous.ResolutionId is not null &&
            Count(database, "SELECT COUNT(*) FROM ProfessorPaper WHERE PaperId=$paper", ("$paper", ambiguous.PaperId)) == 0,
            "duplicate matching author names are held for paper attribution review");
        var unknown = service.ImportPaper(paper with { Id = "p04c-paper-unknown", ExternalId = "doi:10.1000/unknown", Authors = ["Unrelated Author"] }, professorId);
        Check(unknown.State == PaperAttributionState.NeedsVerification &&
            Count(database, "SELECT COUNT(*) FROM PaperAttributionResolution WHERE PaperId=$paper AND State='Pending'", ("$paper", unknown.PaperId)) == 1,
            "unmatched paper authors remain visible as pending rather than being linked by name proximity");

        var read = service.RecordPaperRead(new PaperReadInput(attribution.PaperId, null, 1, 3, "Pages", "pdfpig-v1",
            Encoding.UTF8.GetBytes("page text")));
        var unreadable = service.RecordPaperRead(new PaperReadInput(attribution.PaperId, "scan-hash", null, null, "Unreadable", "pdfpig-v1"));
        Check(read.TextArtifactId is not null && unreadable.TextArtifactId is null &&
            Count(database, "SELECT COUNT(*) FROM PaperReadSegment WHERE PaperId=$paper AND ReadScope IN ('Pages','Unreadable')", ("$paper", attribution.PaperId)) == 2,
            "paper reading scope preserves page range, text artifact and unreadable state");

        var evaluationInput = new ProfessorEvaluationInput(professorId, "https://review.example.org/test", "AnonymousReview", null,
            "The review mentions a heavy workload.", "{\"topics\":[\"workload\"]}", "Anonymous review text", DateTimeOffset.UtcNow);
        var evaluation = service.ImportEvaluation(evaluationInput);
        var evaluationRepeat = service.ImportEvaluation(evaluationInput);
        Check(evaluation.EvaluationId == evaluationRepeat.EvaluationId &&
            Count(database, "SELECT COUNT(*) FROM ProfessorEvaluation WHERE ProfessorId=$prof", ("$prof", professorId)) == 1 &&
            Scalar(database, "SELECT VerificationState FROM ProfessorEvaluation WHERE Id=$id", ("$id", evaluation.EvaluationId)) == "Unverified",
            "third-party evaluation is deduplicated and remains an unverified independent source");
        Console.WriteLine("PASS: homepage evidence, unique paper attribution, paper read scopes, evaluation provenance and idempotency");
        return Task.CompletedTask;
    }

    private static void InsertProfessor(LocalDatabase database, string id, string name, string institution, string? homepage, string? email)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO Professor(Id,Name,Institution,Homepage,Email,EvidenceJson,UpdatedAt)
            VALUES($id,$name,$institution,$homepage,$email,'[]',$now)
            """, ("$id", id), ("$name", name), ("$institution", institution), ("$homepage", homepage), ("$email", email),
            ("$now", DateTimeOffset.UtcNow.ToString("O")));
        command.ExecuteNonQuery();
    }

    private static int Count(LocalDatabase database, string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, sql, parameters);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string? Scalar(LocalDatabase database, string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, sql, parameters);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
