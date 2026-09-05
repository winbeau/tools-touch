using System.Text.Json;

namespace ToolsTouch.Core;

public sealed record ToolContext(string Kind, string? ProfileId, string? ProfessorId, string? RunId = null);

public sealed class ToolDispatcher(ResearchStore store, LibraryService library, OutreachService outreach,
    IWebSearch search, IPaperSearch papers, PublicWeb web)
{
    public static readonly string[] AllowedTools = ["search_web", "fetch_page", "search_professors", "search_papers", "read_paper", "read_user_profile", "save_professor", "create_outreach_draft"];

    public async Task<object> ExecuteAsync(string tool, JsonElement input, string callId, CancellationToken cancellationToken, ToolContext? context = null)
    {
        try
        {
            if (!AllowedTools.Contains(tool, StringComparer.Ordinal)) throw new InvalidOperationException("TOOL_NOT_ALLOWED");
            Validate(tool, input);
            cancellationToken.ThrowIfCancellationRequested();
            object result;
            switch (tool)
            {
                case "search_web":
                    result = await search.SearchAsync(Text(input, "query"), input.GetProperty("limit").GetInt32(),
                        input.TryGetProperty("domains", out var domains) ? domains.EnumerateArray().Select(domain => domain.GetString()!).ToArray() : null, cancellationToken);
                    break;
                case "fetch_page":
                    var page = await web.FetchAsync(Text(input, "url"), cancellationToken);
                    library.SaveSource(page);
                    result = page;
                    break;
                case "search_professors":
                    result = store.SearchProfessors(Text(input, "query"), input.GetProperty("limit").GetInt32());
                    break;
                case "search_papers":
                    var professorId = OptionalText(input, "professor_id");
                    var professor = professorId == null ? null : store.GetProfessor(professorId);
                    var found = await papers.SearchAsync(Text(input, "query"), input.GetProperty("limit").GetInt32(), cancellationToken);
                    // A query mentioning a professor is not proof of authorship; only link matching author names.
                    result = found.Select(paper => library.SavePaper(paper,
                        professor != null && paper.Authors.Any(author => author.Equals(professor.Name, StringComparison.OrdinalIgnoreCase)) ? professorId : null)).ToArray();
                    break;
                case "read_paper":
                    result = await library.ReadPaperAsync(Text(input, "paper_id"), input.GetProperty("start_page").GetInt32(), input.GetProperty("max_pages").GetInt32(), cancellationToken);
                    break;
                case "read_user_profile":
                    var profile = context == null ? library.GetProfile(confirmedOnly: true) :
                        context.ProfileId == null ? null : library.GetProfile(context.ProfileId, confirmedOnly: true);
                    result = profile == null ? new { available = false, note = "No confirmed profile; personal matching is unavailable." } :
                        (object)new { available = true, profile_id = profile.Id, version = profile.Version,
                            experiences = JsonSerializer.Deserialize<JsonElement>(profile.ExperiencesJson) };
                    break;
                case "save_professor":
                    var evidence = ReadEvidence(input);
                    RequireSources(evidence);
                    var email = OptionalText(input, "email");
                    if (!string.IsNullOrWhiteSpace(email) && !evidence.Any(item => library.SourceText(item.Url)?.Contains(email, StringComparison.OrdinalIgnoreCase) == true))
                        throw new InvalidOperationException("EMAIL_SOURCE_REQUIRED");
                    result = store.SaveProfessor(Text(input, "name"), Text(input, "institution"), Text(input, "homepage"), email, evidence, callId);
                    break;
                case "create_outreach_draft":
                    if (context?.Kind != "Draft") throw new InvalidOperationException("DRAFT_NOT_REQUESTED");
                    var draftKey = context.RunId == null ? callId : "task:" + context.RunId + ":draft";
                    var existingDraft = outreach.FindByIdempotencyKey(draftKey);
                    if (existingDraft != null)
                    {
                        if (existingDraft.ProfessorId != context.ProfessorId) throw new InvalidOperationException("WRONG_DRAFT_TARGET");
                        result = new { id = existingDraft.Id, version = existingDraft.Version, existing = true };
                        break;
                    }
                    var draftEvidence = ReadEvidence(input);
                    RequireSources(draftEvidence);
                    var target = store.GetProfessor(Text(input, "professor_id"));
                    if (target.Id != context.ProfessorId) throw new InvalidOperationException("WRONG_DRAFT_TARGET");
                    var confirmed = context.ProfileId == null ? null : library.GetProfile(context.ProfileId, confirmedOnly: true);
                    var createdDraft = outreach.CreateDraft(target.Id, target.Email ?? "", Text(input, "subject"), Text(input, "body"),
                        confirmed == null ? null : library.VerifyCv(confirmed), draftKey, ResearchStore.Serialize(draftEvidence), confirmed?.Id);
                    result = new { id = createdDraft.Id, version = createdDraft.Version, existing = false };
                    break;
                default: throw new InvalidOperationException("TOOL_NOT_ALLOWED");
            }
            return new { ok = true, data = result };
        }
        catch (OperationCanceledException) { return new { ok = false, error = new { code = "CANCELLED" } }; }
        catch (Exception error)
        {
            var code = error is InvalidOperationException or KeyNotFoundException &&
                error.Message.All(character => char.IsAsciiLetterUpper(character) || character == '_') ? error.Message :
                error is HttpRequestException ? "SOURCE_UNAVAILABLE" : "TOOL_FAILED";
            return new { ok = false, error = new { code } };
        }
    }

    private void RequireSources(Evidence[] evidence)
    {
        foreach (var item in evidence)
            if (library.SourceText(item.Url) == null && !library.ListPapers().Any(paper => paper.SourceUrl == item.Url))
                throw new InvalidOperationException("FETCH_EVIDENCE_FIRST");
    }
    private static Evidence[] ReadEvidence(JsonElement input) => JsonSerializer.Deserialize<Evidence[]>(input.GetProperty("evidence"), ResearchStore.Json)!;
    private static string Text(JsonElement input, string name) => input.GetProperty(name).GetString()!;
    private static string? OptionalText(JsonElement input, string name) => input.TryGetProperty(name, out var value) ? value.GetString() : null;

    // Revalidate on the C# boundary even if AgentHost has already checked its JSON Schema.
    public static void Validate(string tool, JsonElement input)
    {
        var fields = tool switch
        {
            "search_web" => new[] { "query", "limit", "domains?" },
            "fetch_page" => ["url"], "search_professors" => ["query", "limit"],
            "search_papers" => ["query", "professor_id?", "limit"],
            "read_paper" => ["paper_id", "start_page", "max_pages"], "read_user_profile" => [],
            "save_professor" => ["name", "institution", "homepage", "email?", "evidence"],
            "create_outreach_draft" => ["professor_id", "subject", "body", "evidence"],
            _ => throw new InvalidOperationException("TOOL_NOT_ALLOWED")
        };
        Shape(input, fields);
        foreach (var property in input.EnumerateObject())
        {
            switch (property.Name)
            {
                case "limit": Number(property.Value, 1, tool == "search_papers" ? 10 : 20); break;
                case "start_page": Number(property.Value, 1, int.MaxValue); break;
                case "max_pages": Number(property.Value, 1, 10); break;
                case "domains":
                    if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() > 10) Invalid();
                    foreach (var domain in property.Value.EnumerateArray()) String(domain, 1, 10000);
                    break;
                case "evidence":
                    if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() is < 1 or > 30) Invalid();
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        Shape(item, ["url", "claim"]);
                        String(item.GetProperty("claim"), 1, 10000);
                        String(item.GetProperty("url"), 1, 2048);
                        PublicWeb.ValidateUri(item.GetProperty("url").GetString()!);
                    }
                    break;
                case "url": case "homepage":
                    String(property.Value, 1, 2048);
                    PublicWeb.ValidateUri(property.Value.GetString()!);
                    break;
                default:
                    String(property.Value, property.Name == "email" ? 0 : 1, property.Name switch
                    { "professor_id" or "paper_id" => 128, "subject" => 300, "email" => 254, _ => 10000 });
                    break;
            }
        }
    }
    private static void Shape(JsonElement input, string[] fields)
    {
        if (input.ValueKind != JsonValueKind.Object) Invalid();
        var names = input.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Distinct().Count() != names.Length || names.Any(name => !fields.Any(field => field.TrimEnd('?') == name)) ||
            fields.Any(field => !field.EndsWith('?') && !input.TryGetProperty(field, out _))) Invalid();
    }
    private static void String(JsonElement value, int min, int max)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length < min || value.GetString()!.Length > max) Invalid();
    }
    private static void Number(JsonElement value, int min, int max)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < min || number > max) Invalid();
    }
    private static void Invalid() => throw new InvalidOperationException("INVALID_TOOL_INPUT");
}
