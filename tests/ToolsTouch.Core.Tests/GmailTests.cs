using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MimeKit;
using ToolsTouch.Core;

static class GmailTests
{
    public static async Task RunAsync(LocalDatabase database)
    {
        var secrets = new MemorySecrets();
        string? challenge = null;
        var tokenCalls = 0;
        using var oauthHttp = new HttpClient(new Handler(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/token")
            {
                tokenCalls++;
                var form = Query(await request.Content!.ReadAsStringAsync());
                Check(form["grant_type"] == "authorization_code" && GmailAuth.Challenge(form["code_verifier"]) == challenge, "OAuth uses actual PKCE verifier");
                return Json(new { access_token = "test-access", refresh_token = "test-refresh", expires_in = 3600, scope = GmailAuth.Scopes });
            }
            Check(request.Headers.Authorization?.Parameter == "test-access", "OAuth profile uses acquired token");
            return Json(new { emailAddress = "sender@example.org" });
        }));
        var auth = new GmailAuth(oauthHttp, secrets, () => new("test-client", "test-client-secret"));
        Task? browser = null;
        await auth.ConnectAsync(uri =>
        {
            var query = Query(uri.Query.TrimStart('?')); challenge = query["code_challenge"];
            Check(query["scope"] == GmailAuth.Scopes && query["code_challenge_method"] == "S256", "only required Gmail scopes requested");
            browser = Task.Run(async () =>
            {
                using var callback = new HttpClient();
                using var preconnect = new System.Net.Sockets.TcpClient();
                var redirect = new Uri(query["redirect_uri"]);
                await preconnect.ConnectAsync(redirect.Host, redirect.Port);
                using var invalid = await callback.GetAsync(query["redirect_uri"] + "?code=ignored&state=wrong");
                Check(invalid.StatusCode == HttpStatusCode.BadRequest, "callback rejects wrong state");
                using var valid = await callback.GetAsync(query["redirect_uri"] + "?code=test-code&state=" + Uri.EscapeDataString(query["state"]));
                Check(valid.IsSuccessStatusCode, "callback accepts matching state");
            });
        });
        await browser!;
        Check(auth.Account == "sender@example.org" && await auth.AccessTokenAsync(default) == "test-access" && tokenCalls == 1, "OAuth account and in-memory access caching");
        Check(secrets.Value!.Contains("test-refresh") && !secrets.Value.Contains("test-access"), "persist refresh credential only");

        await auth.ConnectAsync(_ => throw new Exception("Repeated connect opened a second browser"));
        var reopened = new GmailAuth(oauthHttp, secrets, () => new("test-client", "test-client-secret"));
        await reopened.ConnectAsync(_ => throw new Exception("Reopened app requested Gmail authorization again"));
        Check(reopened.Account == "sender@example.org" && tokenCalls == 1, "saved Gmail identity was not reused");

        var outreach = new OutreachService(database);
        var draft = outreach.CreateDraft("prof", "recipient@example.org", "研究合作", "Hello\nResearch proposal", null, "gmail-send");
        var statusCode = HttpStatusCode.OK;
        string? rfcMessageId = null;
        var found = false;
        var sends = 0;
        using var apiHttp = new HttpClient(new Handler(async request =>
        {
            Check(request.Headers.Authorization?.Parameter == "test-access", "Gmail authenticated request");
            if (request.Method == HttpMethod.Post)
            {
                sends++;
                Check(request.RequestUri!.AbsolutePath.EndsWith("/messages/send"), "only Gmail send endpoint");
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
                var raw = payload.RootElement.GetProperty("raw").GetString()!;
                Check(!raw.Contains('+') && !raw.Contains('/'), "base64url encoded MIME");
                var decoded = raw.Replace('-', '+').Replace('_', '/'); decoded += new string('=', (4 - decoded.Length % 4) % 4);
                using var message = MimeMessage.Load(new MemoryStream(Convert.FromBase64String(decoded)));
                Check(message.To.Mailboxes.Single().Address == "recipient@example.org" && message.Subject == "研究合作", "MIME recipient and Unicode subject round trip");
                rfcMessageId = message.MessageId;
                return Json(new { id = "sent-message", threadId = "sent-thread" }, statusCode);
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/messages"))
                return Json(new { messages = found ? new[] { new { id = "sent-message" } } : [] });
            if (request.RequestUri.AbsolutePath.Contains("/messages/"))
                return Json(new { id = "sent-message", threadId = "sent-thread", labelIds = new[] { "SENT" }, payload = new { headers = new[] { new { name = "Message-ID", value = "<" + rfcMessageId + ">" }, new { name = "To", value = "recipient@example.org" } } } });
            return Json(new { messages = new[]
            {
                new { id = "sent-message", internalDate = "100", labelIds = new[] { "SENT" }, payload = new { headers = new[] { new { name = "From", value = "sender@example.org" } } } },
                new { id = "reply", internalDate = "200", labelIds = new[] { "INBOX" }, payload = new { headers = new[] { new { name = "From", value = "recipient@example.org" } } } }
            } });
        }));
        var gmail = new GmailService(apiHttp, auth);
        // Simulate a server-side failure: the application must not infer that nothing was sent.
        statusCode = HttpStatusCode.InternalServerError;
        try { await outreach.SendAsync(draft.Id, gmail, senderAccount: auth.Account); throw new Exception("Expected 500"); } catch (HttpRequestException) { }
        Check(outreach.Get(draft.Id).State == "Unknown" && sends == 1, "5xx has ambiguous outcome with no retry");
        Check(!await outreach.ReconcileAsync(draft.Id, gmail) && outreach.Get(draft.Id).State == "Unknown", "not found does not prove non-delivery");
        found = true;
        Check(await outreach.ReconcileAsync(draft.Id, gmail) && outreach.Get(draft.Id).State == "Sent", "matching sent MIME message reconciles unknown result");
        Check(await outreach.SyncRepliesAsync(gmail) == 1 && outreach.ReplyCount() == 1, "reply thread persisted");
        Check(outreach.History("prof").Any(item => item.Id == draft.Id && item.State == "Sent" && item.ReplyState == "Replied"), "professor history joins account-scoped reply state");
        var rejected = outreach.CreateDraft("prof", "recipient@example.org", "研究合作", "Hello", null, "gmail-rejected");
        statusCode = HttpStatusCode.BadRequest;
        try { await outreach.SendAsync(rejected.Id, gmail, senderAccount: auth.Account); throw new Exception("Expected rejection"); } catch (SendRejectedException) { }
        Check(outreach.Get(rejected.Id).State == "Failed", "definite Gmail 400 rejected");
        var revised = outreach.Edit(rejected.Id, rejected.Revision, rejected.Recipient, rejected.Subject, "Edited", null);
        try { await outreach.SendAsync(revised.Id, gmail, expectedRevision: rejected.Revision); throw new Exception("Expected stale review failure"); }
        catch (InvalidOperationException error) when (error.Message == "DRAFT_CHANGED_REVIEW_REQUIRED") { }
        Check(sends == 2, "stale review does not dispatch another message");
        Console.WriteLine("PASS: simulated Google OAuth callback/state/PKCE, scoped credential persistence, MIME, Gmail failure classification, reconciliation, reply sync, stale send review. No real mail sent.");
    }
    private static Dictionary<string, string> Query(string value) => value.Split('&').Select(part => part.Split('=', 2)).ToDictionary(parts => Uri.UnescapeDataString(parts[0]), parts => Uri.UnescapeDataString(parts[1].Replace('+', ' ')));
    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
    private sealed class MemorySecrets : ISecretStore
    {
        public string? Value { get; private set; }
        public string? Read(string name) => Value;
        public void Write(string name, string value) => Value = value;
        public void Delete(string name) => Value = null;
    }
}
