using System.Net;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace ToolsTouch.Core;

public sealed record SearchHit(string Title, string Url, string Snippet);
public interface IWebSearch
{
    Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, string[]? domains, CancellationToken cancellationToken);
}

public sealed class BraveWebSearch(HttpClient client, Func<string?> getApiKey) : IWebSearch
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset nextRequest;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int limit, string[]? domains, CancellationToken cancellationToken)
    {
        var apiKey = getApiKey();
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("WEB_SEARCH_NOT_CONFIGURED");
        if (domains is { Length: > 0 })
        {
            if (domains.Any(domain => Uri.CheckHostName(domain) != UriHostNameType.Dns)) throw new InvalidOperationException("INVALID_SEARCH_DOMAIN");
            query += " (" + string.Join(" OR ", domains.Select(domain => "site:" + domain)) + ")";
        }
        await gate.WaitAsync(cancellationToken);
        try
        {
            var delay = nextRequest - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            nextRequest = DateTimeOffset.UtcNow.AddSeconds(1);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.search.brave.com/res/v1/web/search?q={Uri.EscapeDataString(query)}&count={Math.Clamp(limit, 1, 20)}");
            request.Headers.Add("X-Subscription-Token", apiKey);
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                nextRequest = DateTimeOffset.UtcNow + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60));
                throw new InvalidOperationException("WEB_SEARCH_RATE_LIMITED");
            }
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!json.RootElement.TryGetProperty("web", out var web) || !web.TryGetProperty("results", out var results)) return [];
            return results.EnumerateArray().Take(limit).Select(item => new SearchHit(item.GetProperty("title").GetString()!,
                item.GetProperty("url").GetString()!, item.GetProperty("description").GetString() ?? "")).ToArray();
        }
        finally { gate.Release(); }
    }
}

public interface IPaperSearch
{
    Task<IReadOnlyList<Paper>> SearchAsync(string query, int limit, CancellationToken cancellationToken = default);
}

public sealed class CrossrefSearch(HttpClient client) : IPaperSearch
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset nextRequest;

    public async Task<IReadOnlyList<Paper>> SearchAsync(string query, int limit, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var delay = nextRequest - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.crossref.org/works?query.bibliographic={Uri.EscapeDataString(query)}&rows={Math.Clamp(limit, 1, 10)}");
            request.Headers.UserAgent.ParseAdd("ToolsTouch/0.1");
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                nextRequest = DateTimeOffset.UtcNow + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60));
                throw new InvalidOperationException("PAPER_SEARCH_RATE_LIMITED");
            }
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var papers = new List<Paper>();
            foreach (var item in json.RootElement.GetProperty("message").GetProperty("items").EnumerateArray())
            {
                var doi = item.GetProperty("DOI").GetString()!;
                var title = item.TryGetProperty("title", out var titles) && titles.GetArrayLength() > 0 ? titles[0].GetString()! : doi;
                var authors = item.TryGetProperty("author", out var authorList) ? authorList.EnumerateArray().Select(author =>
                    string.Join(" ", new[] { author.TryGetProperty("given", out var given) ? given.GetString() : null,
                        author.TryGetProperty("family", out var family) ? family.GetString() : null }.Where(part => part != null))).ToArray() : [];
                int? year = item.TryGetProperty("published", out var published) ? published.GetProperty("date-parts")[0][0].GetInt32() : null;
                var abstractText = item.TryGetProperty("abstract", out var summary) ? summary.GetString() : null;
                papers.Add(new(Guid.NewGuid().ToString("N"), doi, title, authors, year, abstractText, "https://doi.org/" + doi, null, "MetadataOnly"));
            }
            return papers;
        }
        finally
        {
            if (nextRequest < DateTimeOffset.UtcNow.AddSeconds(1)) nextRequest = DateTimeOffset.UtcNow.AddSeconds(1);
            gate.Release();
        }
    }
}

public sealed class ArxivSearch(HttpClient client) : IPaperSearch
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset nextRequest;
    public async Task<IReadOnlyList<Paper>> SearchAsync(string query, int limit, CancellationToken cancellationToken = default)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(16)
            .Select(term => "all:\"" + term.Replace("\"", "").Replace("\\", "") + "\"");
        var expression = string.Join(" AND ", terms);
        if (expression.Length == 0) throw new InvalidOperationException("EMPTY_PAPER_QUERY");
        await gate.WaitAsync(cancellationToken);
        try
        {
            var delay = nextRequest - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://export.arxiv.org/api/query?search_query={Uri.EscapeDataString(expression)}&start=0&max_results={Math.Clamp(limit, 1, 10)}&sortBy=relevance");
            request.Headers.UserAgent.ParseAdd("ToolsTouch/0.1");
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                nextRequest = DateTimeOffset.UtcNow + (response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60));
                throw new InvalidOperationException("PAPER_SEARCH_RATE_LIMITED");
            }
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
            var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
            XNamespace atom = "http://www.w3.org/2005/Atom";
            var papers = new List<Paper>();
            foreach (var entry in document.Root!.Elements(atom + "entry"))
            {
                if (!Uri.TryCreate(entry.Element(atom + "id")?.Value, UriKind.Absolute, out var uri) || uri.Host != "arxiv.org" || !uri.AbsolutePath.StartsWith("/abs/"))
                    throw new InvalidOperationException("INVALID_ARXIV_RESULT");
                var identifier = uri.AbsolutePath[5..];
                var title = entry.Element(atom + "title")?.Value.Trim() ?? throw new InvalidOperationException("INVALID_ARXIV_RESULT");
                var authors = entry.Elements(atom + "author").Select(author => author.Element(atom + "name")!.Value.Trim()).ToArray();
                var year = DateTimeOffset.TryParse(entry.Element(atom + "published")?.Value, out var date) ? (int?)date.Year : null;
                papers.Add(new(Guid.NewGuid().ToString("N"), "arxiv:" + identifier, title, authors, year,
                    entry.Element(atom + "summary")?.Value.Trim(), "https://arxiv.org/abs/" + identifier, null, "MetadataOnly"));
            }
            return papers;
        }
        finally
        {
            if (nextRequest < DateTimeOffset.UtcNow.AddSeconds(3)) nextRequest = DateTimeOffset.UtcNow.AddSeconds(3);
            gate.Release();
        }
    }
}

public sealed class ScholarlySearch(IPaperSearch primary, IPaperSearch fallback) : IPaperSearch
{
    public async Task<IReadOnlyList<Paper>> SearchAsync(string query, int limit, CancellationToken cancellationToken = default)
    {
        try
        {
            var papers = await primary.SearchAsync(query, limit, cancellationToken);
            if (papers.Count > 0) return papers;
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (InvalidOperationException error) when (error.Message == "PAPER_SEARCH_RATE_LIMITED") { }
        return await fallback.SearchAsync(query, limit, cancellationToken);
    }
}
