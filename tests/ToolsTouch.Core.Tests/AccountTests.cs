using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ToolsTouch.Core;

static class AccountTests
{
    public static async Task RunAsync(string directory)
    {
        var environment = new Dictionary<string, string?> { ["NO_PROXY"] = "internal.example" };
        AgentProxy.Configure(environment, new WebProxy("http://127.0.0.1:12345"));
        Check(environment["HTTPS_PROXY"] == "http://127.0.0.1:12345/" && environment["NODE_USE_ENV_PROXY"] == "1", "model host lost system proxy");
        Check(environment["NO_PROXY"]!.Contains("127.0.0.1") && environment["NO_PROXY"]!.Contains("internal.example"), "OAuth callback proxy bypass missing");
        environment["HTTPS_PROXY"] = "http://explicit.example:8080";
        AgentProxy.Configure(environment, new WebProxy("http://127.0.0.1:12345"));
        Check(environment["HTTPS_PROXY"] == "http://explicit.example:8080", "explicit proxy overwritten");
        var log = new DiagnosticLog(directory);
        const string secret = "synthetic-secret-should-never-be-logged";
        var error = new HttpRequestException("https://example.org?code=" + secret, new Exception(secret));
        log.Write("auth", "failed", DiagnosticLog.ErrorCode(error), error);
        var lines = string.Join("", Directory.GetFiles(log.DirectoryPath).Select(File.ReadAllText));
        Check(!lines.Contains(secret) && lines.Contains("NETWORK_FAILED"), "diagnostic error leaked sensitive details");
        var configPath = Path.Combine(directory, "google-test.json");
        Expect("GMAIL_CLIENT_NOT_CONFIGURED", () => GoogleClient.FromFile(""));
        Expect("GMAIL_CLIENT_FILE_MISSING", () => GoogleClient.FromFile(configPath));
        foreach (var (json, expected) in new[] { ("broken", "GMAIL_CLIENT_INVALID"), ("[]", "GMAIL_CLIENT_INVALID"), ("{\"web\":{}}", "GMAIL_CLIENT_NOT_DESKTOP"), ("{\"installed\":{}}", "GMAIL_CLIENT_INVALID") })
        {
            File.WriteAllText(configPath, json);
            Expect(expected, () => GoogleClient.FromFile(configPath));
        }
        File.WriteAllText(configPath, "{\"installed\":{\"client_id\":\"test.apps.googleusercontent.com\",\"client_secret\":\"synthetic\"}}");
        Check(GoogleClient.FromFile(configPath).ClientId == "test.apps.googleusercontent.com", "valid desktop client rejected");
        using var unusedHttp = new HttpClient();
        var damaged = new GmailAuth(unusedHttp, new Secrets(true), () => new("test", ""));
        Check(damaged.Account == null && damaged.CredentialError == "GMAIL_CREDENTIAL_UNREADABLE", "damaged credential must allow application startup and reconnect");
        var incomplete = new GmailAuth(unusedHttp, new Secrets { Value = "{\"email\":\"sender@example.org\"}" }, () => new("test", ""));
        Check(incomplete.Account == null && incomplete.CredentialError == "GMAIL_CREDENTIAL_UNREADABLE", "incomplete saved identity must not unlock the app");
        foreach (var scenario in new[] { "denied", "invalid_client", "invalid_grant", "scope", "disabled", "cancel" })
        {
            var secrets = new Secrets();
            using var http = new HttpClient(new Handler(request =>
            {
                if (scenario is "invalid_client" or "invalid_grant") return Json(new { error = scenario, error_description = secret }, HttpStatusCode.BadRequest);
                if (request.RequestUri!.AbsolutePath == "/token") return Json(new { access_token = secret, refresh_token = secret, expires_in = 3600, scope = scenario == "scope" ? "" : GmailAuth.Scopes });
                return Json(new { error = new { status = "SERVICE_DISABLED" } }, HttpStatusCode.Forbidden);
            }));
            var auth = new GmailAuth(http, secrets, () => new("test", ""));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task? browser = null;
            var expected = scenario switch { "denied" => "GMAIL_AUTH_DENIED", "invalid_client" => "GMAIL_OAUTH_INVALID_CLIENT", "invalid_grant" => "GMAIL_OAUTH_INVALID_GRANT", "scope" => "GMAIL_REQUIRED_SCOPES_MISSING", "disabled" => "GMAIL_API_DISABLED", _ => "cancel" };
            try
            {
                await auth.ConnectAsync(uri =>
                {
                    if (scenario == "cancel") { cancellation.Cancel(); return; }
                    var query = uri.Query[1..].Split('&').Select(pair => pair.Split('=', 2)).ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
                    browser = Task.Run(async () =>
                    {
                        using var callback = new HttpClient();
                        using var result = await callback.GetAsync(query["redirect_uri"] + "?state=" + query["state"] + (scenario == "denied" ? "&error=access_denied" : "&code=synthetic"));
                    });
                }, cancellation.Token);
                throw new Exception("Expected " + expected);
            }
            catch (InvalidOperationException failure) when (failure.Message == expected) { }
            catch (OperationCanceledException) when (scenario == "cancel" && cancellation.IsCancellationRequested) { }
            if (browser != null) await browser;
            Check(secrets.Value == null && auth.Account == null, "failed authorization persisted credentials");
        }
        Console.WriteLine("PASS: safe diagnostic metadata, Google client validation, corrupt credential recovery, denied/cancelled OAuth, invalid client/grant, missing scopes and disabled Gmail API. Offline fixtures only.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Expect(string code, Action action)
    {
        try { action(); throw new Exception("Expected " + code); }
        catch (InvalidOperationException error) when (error.Message == code) { }
    }
    private static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
    private sealed class Secrets(bool damaged = false) : ISecretStore
    {
        public string? Value;
        public string? Read(string name) => damaged ? throw new CryptographicException("synthetic") : Value;
        public void Write(string name, string value) => Value = value;
        public void Delete(string name) => Value = null;
    }
}
