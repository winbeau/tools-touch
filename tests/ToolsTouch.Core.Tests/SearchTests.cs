using System.Net;
using System.Text;
using System.Xml;
using ToolsTouch.Core;

static class SearchTests
{
    public static async Task RunAsync()
    {
        const string feed = """
            <feed xmlns="http://www.w3.org/2005/Atom"><entry>
            <id>http://arxiv.org/abs/1803.10122v4</id><title>World Models</title>
            <author><name>David Ha</name></author><author><name>Jürgen Schmidhuber</name></author>
            <published>2018-03-27T00:00:00Z</published><summary>Research abstract</summary>
            </entry></feed>
            """;
        using var http = new HttpClient(new FeedHandler(feed));
        var papers = await new ArxivSearch(http).SearchAsync("World Models", 3);
        if (papers.Single().ExternalId != "arxiv:1803.10122v4" || papers[0].Authors.Length != 2 || papers[0].ParseStatus != "MetadataOnly")
            throw new Exception("arXiv identity/version/authors must survive Atom parsing");
        using var unsafeHttp = new HttpClient(new FeedHandler("<!DOCTYPE feed [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><feed xmlns='http://www.w3.org/2005/Atom'>&x;</feed>"));
        try { await new ArxivSearch(unsafeHttp).SearchAsync("World Models", 3); throw new Exception("Expected forbidden DTD"); }
        catch (XmlException) { }
        var fallback = new StubSearch(papers);
        var combined = new ScholarlySearch(new StubSearch(null), fallback);
        if ((await combined.SearchAsync("World Models", 3)).Count != 1 || fallback.Calls != 1) throw new Exception("Primary failure should use scholarly fallback");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await combined.SearchAsync("World Models", 3, cancelled.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
        if (fallback.Calls != 1) throw new Exception("Cancellation must not trigger fallback request");
        Console.WriteLine("PASS: arXiv metadata parsing/version, XML entity rejection, scholarly fallback and cancellation");
    }
    private sealed class FeedHandler(string xml) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "application/atom+xml") });
    }
    private sealed class StubSearch(IReadOnlyList<Paper>? result) : IPaperSearch
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<Paper>> SearchAsync(string query, int limit, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            return result == null ? throw new HttpRequestException("unavailable") : Task.FromResult(result);
        }
    }
}
