using System.Security.Cryptography;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ToolsTouch.Application;
using ToolsTouch.Core;

namespace ToolsTouch.Infrastructure.Collection;

public sealed class OfficialFacultyDirectoryAdapter(PublicWeb web) : IFacultyDirectorySourceAdapter
{
    public SourceAdapterDescriptor Descriptor { get; } = new(
        "official-faculty-directory", "official-directory-v1", "Official faculty directory", ["directory"]);

    public IReadOnlyList<FacultyCrawlTarget> DiscoverScope(FacultyDirectoryScope scope) =>
        [new FacultyCrawlTarget(scope.NormalizedSeedUrl, "directory")];

    public async Task<FacultySourcePage> FetchAsync(FacultyCrawlTarget target, CancellationToken cancellationToken = default)
    {
        var downloaded = await web.DownloadAsync(target.NormalizedUrl, cancellationToken: cancellationToken);
        return new(target.NormalizedUrl, downloaded.Url.AbsoluteUri, downloaded.MediaType, downloaded.Bytes,
            DateTimeOffset.UtcNow);
    }

    public FacultyDirectoryPage Parse(FacultyDirectoryScope scope, FacultySourcePage page)
    {
        if (page.MediaType is not ("text/html" or "application/xhtml+xml"))
            throw new InvalidOperationException("DIRECTORY_HTML_REQUIRED");

        var seed = PublicWeb.ValidateUri(scope.NormalizedSeedUrl);
        var canonical = PublicWeb.ValidateUri(page.FinalUrl);
        if (!SameOrigin(seed, canonical)) throw new InvalidOperationException("DIRECTORY_ORIGIN_CHANGED");

        var html = new UTF8Encoding(false, true).GetString(page.Content);
        var document = new HtmlParser().ParseDocument(html);
        var candidates = new List<FacultyDirectoryCandidate>();
        foreach (var element in CandidateElements(document))
        {
            var name = FirstText(element, "[data-name], .name, .faculty-name, .teacher-name, .professor-name, h1, h2, h3, h4");
            if (string.IsNullOrWhiteSpace(name)) continue;
            var homepage = Href(element.QuerySelector("a[href]"));
            var role = FirstText(element, "[data-role], .role, .title, .position");
            var mailto = Href(element.QuerySelector("a[href^='mailto:']"));
            var email = mailto is null ? null : mailto["mailto:".Length..].Split('?', 2)[0];
            candidates.Add(new FacultyDirectoryCandidate(
                element.GetAttribute("data-faculty-id") ?? element.GetAttribute("data-external-id"),
                name, homepage, role, element.TextContent, canonical.AbsoluteUri,
                element.GetAttribute("data-department"), email, mailto));
        }

        var targets = new List<FacultyCrawlTarget>();
        foreach (var anchor in document.QuerySelectorAll("a[href]"))
        {
            var href = Href(anchor);
            if (href is null || !Uri.TryCreate(canonical, href, out var uri) || !SameOrigin(seed, uri)) continue;
            var rel = anchor.GetAttribute("rel") ?? "";
            var classes = anchor.GetAttribute("class") ?? "";
            var text = Collapse(anchor.TextContent);
            var isPagination = rel.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(value => value.Equals("next", StringComparison.OrdinalIgnoreCase)) ||
                classes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Any(value => value.Equals("pagination", StringComparison.OrdinalIgnoreCase) || value.Equals("page", StringComparison.OrdinalIgnoreCase)) ||
                text.Contains("下一页", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("next", StringComparison.OrdinalIgnoreCase) ||
                uri.Query.Contains("page=", StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath.Contains("page", StringComparison.OrdinalIgnoreCase);
            if (isPagination) targets.Add(new FacultyCrawlTarget(uri.AbsoluteUri, "directory", canonical.AbsoluteUri));
        }

        return new FacultyDirectoryPage(canonical.AbsoluteUri, canonical.AbsoluteUri,
            candidates, targets.DistinctBy(target => target.NormalizedUrl, StringComparer.OrdinalIgnoreCase).ToArray(),
            ExpectedCount(document));
    }

    public FacultyDirectoryCandidate Normalize(FacultyDirectoryCandidate candidate)
    {
        var name = Collapse(candidate.Name);
        if (name.Length == 0) throw new InvalidOperationException("FACULTY_NAME_REQUIRED");
        var homepage = NormalizeUrl(candidate.HomepageUrl);
        var externalId = Collapse(candidate.ExternalId ?? "");
        if (externalId.Length == 0)
        {
            var identity = name.ToUpperInvariant() + "\0" + (homepage ?? "");
            externalId = "name-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..24];
        }
        return candidate with
        {
            ExternalId = externalId,
            Name = name,
            HomepageUrl = homepage,
            Role = EmptyToNull(candidate.Role),
            RawText = Collapse(candidate.RawText)[..Math.Min(4_000, Collapse(candidate.RawText).Length)],
            SourceUrl = PublicWeb.ValidateUri(candidate.SourceUrl).AbsoluteUri,
            DepartmentName = EmptyToNull(candidate.DepartmentName),
            PublicEmail = NormalizeEmail(candidate.PublicEmail),
            PublicEmailRaw = EmptyToNull(candidate.PublicEmailRaw)
        };
    }

    public Task<SourceAdapterHealth> HealthAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new SourceAdapterHealth(true, "Ready", "Official directory adapter is configured."));

    private static IEnumerable<IElement> CandidateElements(IDocument document)
    {
        var specific = document.QuerySelectorAll("[data-faculty-id], [data-external-id], .faculty-card, .faculty, .teacher, .professor");
        return specific.Length > 0 ? specific : document.QuerySelectorAll("article");
    }

    private static string? FirstText(IElement element, string selector)
    {
        var nested = element.QuerySelector(selector);
        return EmptyToNull(nested?.TextContent ?? element.GetAttribute("data-name"));
    }

    private static string? Href(IElement? element) => EmptyToNull(element?.GetAttribute("href"));

    private static int? ExpectedCount(IDocument document)
    {
        var raw = document.DocumentElement.GetAttribute("data-total") ?? document.Body?.GetAttribute("data-total") ??
            document.QuerySelector("[data-total]")?.GetAttribute("data-total");
        return int.TryParse(raw, out var value) && value >= 0 ? value : null;
    }

    private static string? NormalizeUrl(string? value)
    {
        value = EmptyToNull(value);
        if (value is null) return null;
        try { return PublicWeb.ValidateUri(value).AbsoluteUri; }
        catch (InvalidOperationException) { return null; }
    }

    private static string? NormalizeEmail(string? value)
    {
        value = EmptyToNull(value);
        if (value is null || value.Contains('\r') || value.Contains('\n') ||
            !System.Net.Mail.MailAddress.TryCreate(value, out var address) || address.Address != value)
            return null;
        return address.Address;
    }

    private static bool SameOrigin(Uri expected, Uri actual) =>
        expected.Host.Equals(actual.Host, StringComparison.OrdinalIgnoreCase) && expected.Port == actual.Port;

    private static string? EmptyToNull(string? value)
    {
        var normalized = Collapse(value ?? "");
        return normalized.Length == 0 ? null : normalized;
    }

    private static string Collapse(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
