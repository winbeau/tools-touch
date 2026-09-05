using System.Net;
using System.Text.Json;
using ToolsTouch.Core;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

static class ResearchTests
{
    public static async Task RunAsync(LocalDatabase database, string directory, string[] args)
    {
        var store = new ResearchStore(database);
        var evidence = new[] { new Evidence("https://example.org/research", "Researcher works on world models") };
        var professor = store.SaveProfessor("Researcher", "University", "https://example.org/research/", null, evidence, "prof-write");
        Check(store.SaveProfessor("Researcher", "University", "https://example.org/research/", null, evidence, "prof-write").Id == professor.Id, "professor idempotency");
        Throws(() => store.SaveProfessor("Changed", "University", "https://example.org/research/", null, evidence, "prof-write"));
        Check(store.SearchProfessors("world models").Single().Id == professor.Id, "local evidence search");
        var run = store.CreateRun("Discover", "{}", "{}");
        store.UpdateRun(run.Id, "Running", "Verify", "{\"completedStage\":\"Search\"}");
        store.RecoverInterruptedRuns();
        var recovered = new ResearchStore(database).GetRun(run.Id);
        Check(recovered.State == "Partial" && recovered.Stage == "Verify" && recovered.CheckpointJson!.Contains("Search"), "checkpoint survives interruption");
        using (var connection = database.Open())
        using (var command = LocalDatabase.Command(connection, "SELECT COUNT(DISTINCT Sequence) FROM AgentRunEvent WHERE RunId=$id", ("$id", run.Id)))
            Check(Convert.ToInt32(command.ExecuteScalar()) == 2, "ordered durable state events");

        foreach (var address in new[] { "127.0.0.1", "10.0.0.1", "169.254.169.254", "100.64.0.1", "::1", "::ffff:192.168.1.1", "fc00::1" })
            Check(!PublicWeb.IsPublicAddress(IPAddress.Parse(address)), "private address rejected: " + address);
        Check(PublicWeb.IsPublicAddress(IPAddress.Parse("8.8.8.8")), "public address accepted");
        foreach (var url in new[] { "file:///etc/passwd", "http://localhost", "http://127.1", "https://example.org:8443", "https://user:pass@example.org" })
            Throws(() => PublicWeb.ValidateUri(url));
        Throws(() => ToolDispatcher.Validate("send_email", JsonSerializer.SerializeToElement(new { })));
        Throws(() => ToolDispatcher.Validate("read_user_profile", JsonSerializer.SerializeToElement(new { path = "credentials" })));
        Throws(() => ToolDispatcher.Validate("search_web", JsonSerializer.SerializeToElement(new { query = "test", limit = 0 })));

        using var web = new PublicWeb();
        var library = new LibraryService(database, directory, web);
        var paper = library.SavePaper(new("paper-test", "10.test/paper", "Paper", ["Researcher"], 2026, "An abstract", "https://example.org/paper", null, "MetadataOnly"), professor.Id);
        Check(library.ReadPaper(paper.Id, 1, 2).Coverage == "AbstractOnly", "metadata never reported as full text");
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(600, 800);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        page.AddText("Robotics research project", 12, new PdfPoint(50, 700), font);
        var cvPath = Path.Combine(directory, "source.pdf");
        await File.WriteAllBytesAsync(cvPath, builder.Build());
        var imported = library.ImportCv(cvPath);
        Check(!imported.Confirmed && library.GetProfile(confirmedOnly: true) == null, "CV requires confirmation");
        var confirmed = library.ConfirmProfile(imported.Id, "World models", "Robotics project", "Python");
        var revised = library.ConfirmProfile(confirmed.Id, "World models", "Revised project", "Python");
        Check(revised.Version > confirmed.Version && library.GetProfile(confirmed.Id)!.ExperiencesJson.Contains("Robotics project"), "profile edit preserves old version");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var dispatcher = new ToolDispatcher(store, library, new OutreachService(database), new NoSearch(), new CrossrefSearch(http), web);
        var profileResult = JsonSerializer.Serialize(await dispatcher.ExecuteAsync("read_user_profile", JsonSerializer.SerializeToElement(new { }), "profile-read", default));
        Check(!profileResult.Contains("CvPath") && !profileResult.Contains("cvPath") && !profileResult.Contains("source.pdf") && profileResult.Contains("Revised project"), "model receives experiences but no file paths");
        var pinnedResult = JsonSerializer.Serialize(await dispatcher.ExecuteAsync("read_user_profile", JsonSerializer.SerializeToElement(new { }), "pinned", default, new("Analyze", confirmed.Id, professor.Id)));
        Check(pinnedResult.Contains("Robotics project") && !pinnedResult.Contains("Revised project"), "task reads its pinned profile version");
        var missingResult = JsonSerializer.Serialize(await dispatcher.ExecuteAsync("read_user_profile", JsonSerializer.SerializeToElement(new { }), "missing", default, new("Analyze", null, professor.Id)));
        Check(missingResult.Contains("No confirmed profile"), "task started without CV cannot silently use a later import");
        var invalidProfessor = JsonSerializer.SerializeToElement(new { name = "Researcher", institution = "University", homepage = "https://example.org/research", email = "invented@example.org", evidence }, ResearchStore.Json);
        var invalidResult = JsonSerializer.Serialize(await dispatcher.ExecuteAsync("save_professor", invalidProfessor, "unverified", default));
        Check(invalidResult.Contains("FETCH_EVIDENCE_FIRST"), "unfetched evidence rejected");
        library.SaveSource(new("https://example.org/research", "Researcher", "Researcher works on world models", [], DateTimeOffset.UtcNow));
        invalidResult = JsonSerializer.Serialize(await dispatcher.ExecuteAsync("save_professor", invalidProfessor, "unverified", default));
        Check(invalidResult.Contains("EMAIL_SOURCE_REQUIRED"), "invented email rejected");

        var scripted = new ScriptedBridge(professor.Id, paper.Id);
        var discovery = new DiscoveryService(database, store, library, scripted);
        var analysisTask = discovery.Create("Analyze", "Analyze evidence", professor.Id);
        try { await discovery.ExecuteAsync(analysisTask.Id, "test-model"); throw new Exception("Expected interrupted analysis"); }
        catch (InvalidOperationException error) when (error.Message == "SIMULATED_INTERRUPTION") { }
        Check(store.GetRun(analysisTask.Id).State == "Partial" && scripted.ReadCalls == 1, "failed analysis preserves completed reading stage");
        await discovery.ExecuteAsync(analysisTask.Id, "test-model");
        Check(store.GetRun(analysisTask.Id).State == "Completed" && scripted.ReadCalls == 1 && scripted.AnalysisCalls == 2, "resume skips completed reading stage");
        Check(discovery.LatestAnalysis(professor.Id)!.Contains("Evidence-based summary"), "structured analysis saved");
        var atomicRun = store.CreateRun("Analyze", "{}", "{}");
        Throws(() => store.UpdateRun(atomicRun.Id, "Running", "Analyze", "{\"Analyze\":{}}", saveStageResult: (connection, transaction) =>
        {
            using var command = LocalDatabase.Command(connection, "INSERT INTO ProfessorAnalysis VALUES($id,$prof,NULL,'{}','test','now')", ("$id", atomicRun.Id), ("$prof", professor.Id));
            command.Transaction = transaction; command.ExecuteNonQuery();
            throw new InvalidOperationException("SIMULATED_RESULT_WRITE_FAILURE");
        }));
        Check(store.GetRun(atomicRun.Id).CheckpointJson == null && store.GetRun(atomicRun.Id).State == "Queued", "checkpoint rolls back with failed result write");
        using (var connection = database.Open())
        using (var command = LocalDatabase.Command(connection, "SELECT COUNT(*) FROM ProfessorAnalysis WHERE Id=$id", ("$id", atomicRun.Id)))
            Check(Convert.ToInt32(command.ExecuteScalar()) == 0, "analysis and checkpoint are atomic");

        var draftInput = JsonSerializer.SerializeToElement(new { professor_id = professor.Id, subject = "Research", body = "Evidence-based letter", evidence }, ResearchStore.Json);
        var draftContext = new ToolContext("Draft", confirmed.Id, professor.Id, "resumable-draft-task");
        var firstDraft = JsonSerializer.SerializeToElement(await dispatcher.ExecuteAsync("create_outreach_draft", draftInput, "first-call", default, draftContext));
        var draftId = firstDraft.GetProperty("data").GetProperty("id").GetString()!;
        var draftService = new OutreachService(database);
        var savedDraft = draftService.Get(draftId);
        draftService.Edit(draftId, savedDraft.Revision, "recipient@example.org", savedDraft.Subject, "Human edits", savedDraft.CvPath);
        var retryDraft = JsonSerializer.SerializeToElement(await dispatcher.ExecuteAsync("create_outreach_draft", draftInput, "different-call-after-restart", default, draftContext));
        Check(retryDraft.GetProperty("data").GetProperty("id").GetString() == draftId && draftService.Get(draftId).Body == "Human edits", "resuming same draft task cannot create duplicate or overwrite human edits");

        var busyBridge = new BusyBridge();
        var busyService = new DiscoveryService(database, store, library, busyBridge);
        var waitingRun = busyService.Create("Discover", "World models");
        try { await busyService.ExecuteAsync(waitingRun.Id, "test"); throw new Exception("Expected busy host"); }
        catch (InvalidOperationException error) when (error.Message == "RUN_BUSY") { }
        Check(busyBridge.CancelCalls == 0, "rejected run must not cancel an unrelated active login/run");

        var host = Path.GetFullPath("agent-host/dist/index.js");
        if (!File.Exists(host)) throw new InvalidOperationException("Build AgentHost before core integration tests.");
        await using (var bridge = new AgentBridge("node", host, Path.Combine(directory, "pi"), dispatcher))
        {
            var status = await bridge.RequestAsync(new { type = "status", id = "bridge-test" });
            Check(!status.GetProperty("data").GetProperty("configured").GetBoolean(), "C# bridge receives isolated provider state");
        }
        Console.WriteLine("PASS: professor evidence/idempotency, task checkpoints/events, public URL restrictions, strict C# tools, PDF/CV confirmation/versioning, profile boundary, actual C#↔Pi process");
        if (args.Contains("--live-sources"))
        {
            var arxiv = await new ArxivSearch(http).SearchAsync("World Models Ha Schmidhuber", 3);
            Check(arxiv.Count > 0, "live arXiv results");
            Console.WriteLine("LIVE arXiv: " + string.Join(" | ", arxiv.Select(item => item.Title + " " + item.SourceUrl)));
            var worldModels = arxiv.FirstOrDefault(item => item.Title.Equals("World Models", StringComparison.OrdinalIgnoreCase));
            Check(worldModels != null, "arXiv resolves the World Models paper by title and authors");
            var stored = library.SavePaper(worldModels!, null);
            var fullText = await library.ReadPaperAsync(stored.Id, 1, 2, default);
            Check(fullText.Coverage == "FullTextPages" && fullText.Pages.Any(page => page.Text.Length > 100), "real World Models PDF extraction");
            Console.WriteLine($"LIVE paper: {fullText.SourceUrl}, {fullText.Pages.Length} PDF pages extracted");
            var results = await new CrossrefSearch(http).SearchAsync("World Models Ha Schmidhuber", 3);
            Check(results.Count > 0 && results.All(item => item.SourceUrl.StartsWith("https://doi.org/")), "live Crossref results");
            Console.WriteLine("LIVE Crossref: " + string.Join(" | ", results.Select(item => item.Title + " " + item.SourceUrl)));
            var document = await web.FetchAsync("https://danijar.com/");
            Check(document.Text.Length > 100, "live public page extraction");
            Console.WriteLine($"LIVE public page: {document.Url}, {document.Text.Length} characters, {document.Links.Length} links");
        }
        await File.AppendAllTextAsync(confirmed.CvPath, "tampered");
        Throws(() => library.VerifyCv(confirmed));
        Throws(() => library.ConfirmProfile(confirmed.Id, "Interests", "Projects", "Skills"));
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Throws(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Expected validation failure");
    }
    private sealed class NoSearch : IWebSearch
    {
        public Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, string[]? domains, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("WEB_SEARCH_NOT_CONFIGURED");
    }

    private sealed class ScriptedBridge(string professorId, string paperId) : IAgentBridge
    {
        public event Action<JsonElement>? EventReceived;
        public int ReadCalls { get; private set; }
        public int AnalysisCalls { get; private set; }
        public void SetToolContext(string invocation, ToolContext context) { }
        public void RemoveToolContext(string invocation) { }
        public Task CancelAsync() => Task.CompletedTask;
        public Task<JsonElement> RequestAsync(object command, CancellationToken cancellationToken = default)
        {
            var request = JsonSerializer.SerializeToElement(command, ResearchStore.Json);
            object data;
            if (request.GetProperty("output_kind").GetString() == "research")
            {
                ReadCalls++;
                data = new { state = "Completed", output = new { summary = "Read source", professor_ids = new[] { professorId }, paper_ids = new[] { paperId } } };
            }
            else if (++AnalysisCalls == 1) data = new { state = "Failed", code = "SIMULATED_INTERRUPTION" };
            else data = new { state = "Completed", output = new { professor_id = professorId, research_summary = "Evidence-based summary", personal_match = (string?)null,
                paper_ids = new[] { paperId }, evidence = new[] { new { url = "https://example.org/research", claim = "Researcher works on world models" } } } };
            EventReceived?.Invoke(JsonSerializer.SerializeToElement(new { type = "run_finished", run_id = request.GetProperty("run_id").GetString(), data }, ResearchStore.Json));
            return Task.FromResult(JsonSerializer.SerializeToElement(new { type = "response", id = request.GetProperty("id").GetString(), ok = true }));
        }
    }

    private sealed class BusyBridge : IAgentBridge
    {
        public event Action<JsonElement>? EventReceived { add { } remove { } }
        public int CancelCalls { get; private set; }
        public void SetToolContext(string invocation, ToolContext context) { }
        public void RemoveToolContext(string invocation) { }
        public Task CancelAsync() { CancelCalls++; return Task.CompletedTask; }
        public Task<JsonElement> RequestAsync(object command, CancellationToken cancellationToken = default) => throw new InvalidOperationException("RUN_BUSY");
    }
}
