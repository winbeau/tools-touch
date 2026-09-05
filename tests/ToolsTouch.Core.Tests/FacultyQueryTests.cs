using ToolsTouch.Application;
using ToolsTouch.Core;
using ToolsTouch.Infrastructure.Collection;

static class FacultyQueryTests
{
    public static Task RunAsync(LocalDatabase database, string directory)
    {
        var query = new FacultyQueryService(database);
        var first = query.ListFaculty("Identity University", new PageRequest(1));
        Check(first.Items.Count == 1 && first.NextCursor is not null,
            "faculty list returns a stable first page and cursor");
        var second = query.ListFaculty("Identity University", new PageRequest(10, first.NextCursor));
        Check(second.Items.Count >= 2 && second.Items.Select(item => item.ProfessorId).Intersect(first.Items.Select(item => item.ProfessorId)).Count() == 0,
            "faculty list cursor continues without repeating rows");

        var detail = query.GetDetail("p04c-researcher");
        Check(detail.Faculty.Name == "Test Researcher" && detail.Papers.Count == 1 && detail.Papers[0].Reads.Count == 2 &&
            detail.Evidence.Count >= 2 && detail.Evaluations.Count == 1,
            "faculty detail joins current identity, evidence, papers and evaluations");

        var coverage = query.ListCoverage();
        Check(coverage.Count >= 4 && coverage.Any(row => row.CoverageState == FacultyCoverageState.Partial) &&
            coverage.Any(row => row.CoverageState == FacultyCoverageState.CompleteForDeclaredSources) &&
            coverage.Any(row => row.CoverageState == FacultyCoverageState.UnknownDenominator) &&
            coverage.All(row => row.DiscoveredPageCount >= row.FetchedPageCount),
            "coverage view exposes partial, complete and unknown batches with page counts and failures");
        Console.WriteLine("PASS: faculty list cursor, detail joins and coverage failure states");
        return Task.CompletedTask;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
