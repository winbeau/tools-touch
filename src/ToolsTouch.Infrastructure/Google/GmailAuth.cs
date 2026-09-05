using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ToolsTouch.Application;

namespace ToolsTouch.Core;

public interface ISecretStore
{
    string? Read(string name);
    void Write(string name, string value);
    void Delete(string name);
}
public sealed record GoogleClient(string ClientId, string ClientSecret)
{
    public static GoogleClient FromFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("GMAIL_CLIENT_NOT_CONFIGURED");
        if (!File.Exists(path)) throw new InvalidOperationException("GMAIL_CLIENT_FILE_MISSING");
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("GMAIL_CLIENT_INVALID");
            if (!json.RootElement.TryGetProperty("installed", out var installed)) throw new InvalidOperationException("GMAIL_CLIENT_NOT_DESKTOP");
            if (installed.ValueKind != JsonValueKind.Object || !installed.TryGetProperty("client_id", out var id) || id.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(id.GetString()) || !id.GetString()!.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal))
                throw new InvalidOperationException("GMAIL_CLIENT_INVALID");
            return new(id.GetString()!, installed.TryGetProperty("client_secret", out var secret) && secret.ValueKind == JsonValueKind.String ? secret.GetString()! : "");
        }
        catch (JsonException) { throw new InvalidOperationException("GMAIL_CLIENT_INVALID"); }
    }
}
public sealed record GmailCredential(string ClientId, string RefreshToken, string Email);
public interface IGmailAuthorization
{
    string? Account { get; }
    Task<string> AccessTokenAsync(CancellationToken cancellationToken);
}

public sealed class GmailAuth(HttpClient client, ISecretStore secrets, Func<GoogleClient> clientConfig) : IGmailAccountPort, IGmailAuthorization
{
    public const string Scopes = "https://www.googleapis.com/auth/gmail.send https://www.googleapis.com/auth/gmail.readonly";
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? accessToken;
    private DateTimeOffset expires;
    private GmailCredential? credential;
    private bool credentialLoaded;
    public string? CredentialError { get; private set; }
    public event Action<string>? Progress;
    public string? Account { get { LoadCredential(); return credential?.Email; } }
    private void LoadCredential()
    {
        if (credentialLoaded) return;
        credentialLoaded = true;
        try
        {
            credential = secrets.Read("gmail") is { } json ? JsonSerializer.Deserialize<GmailCredential>(json, ResearchStore.Json) : null;
            if (credential != null && (string.IsNullOrWhiteSpace(credential.ClientId) || string.IsNullOrWhiteSpace(credential.RefreshToken) ||
                string.IsNullOrWhiteSpace(credential.Email) || !System.Net.Mail.MailAddress.TryCreate(credential.Email, out _)))
            { credential = null; CredentialError = "GMAIL_CREDENTIAL_UNREADABLE"; }
        }
        catch (Exception error) when (error is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        { CredentialError = "GMAIL_CREDENTIAL_UNREADABLE"; }
    }
    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public async Task ConnectAsync(Action<Uri> openBrowser, CancellationToken cancellationToken = default, bool forceReauthorize = false)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var config = clientConfig();
            LoadCredential();
            // A repeated connect (including queued clicks) reuses the same Gmail/app identity.
            // Explicit reauthorization remains available when Google has revoked the credential.
            if (!forceReauthorize && credential?.ClientId == config.ClientId) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var token = timeout.Token;
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
            var state = Base64Url(RandomNumberGenerator.GetBytes(32));
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
                var parameters = new Dictionary<string, string> { ["client_id"] = config.ClientId, ["redirect_uri"] = redirect,
                    ["response_type"] = "code", ["scope"] = Scopes, ["state"] = state, ["code_challenge"] = Challenge(verifier),
                    ["code_challenge_method"] = "S256", ["access_type"] = "offline", ["prompt"] = "consent" };
                Progress?.Invoke("waiting_browser");
                openBrowser(new Uri("https://accounts.google.com/o/oauth2/v2/auth?" + string.Join("&", parameters.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)))));
                var code = await ReceiveCodeAsync(listener, state, token);
                Progress?.Invoke("exchanging_code");
                using var response = await client.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = config.ClientId, ["client_secret"] = config.ClientSecret, ["code"] = code,
                    ["code_verifier"] = verifier, ["redirect_uri"] = redirect, ["grant_type"] = "authorization_code"
                }), token);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await OAuthErrorAsync(response, token));
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                var root = json.RootElement;
                if (!root.TryGetProperty("refresh_token", out var refresh)) throw new InvalidOperationException("GMAIL_REFRESH_TOKEN_REQUIRED");
                var granted = root.TryGetProperty("scope", out var grantedScopes) ? grantedScopes.GetString()!.Split(' ') : [];
                if (Scopes.Split(' ').Except(granted).Any()) throw new InvalidOperationException("GMAIL_REQUIRED_SCOPES_MISSING");
                var nextAccess = root.GetProperty("access_token").GetString()!;
                Progress?.Invoke("loading_account");
                using var profileRequest = new HttpRequestMessage(HttpMethod.Get, "https://gmail.googleapis.com/gmail/v1/users/me/profile");
                profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", nextAccess);
                using var profileResponse = await client.SendAsync(profileRequest, token);
                if (!profileResponse.IsSuccessStatusCode) throw new InvalidOperationException(await ProfileErrorAsync(profileResponse, token));
                using var profile = JsonDocument.Parse(await profileResponse.Content.ReadAsStringAsync(token));
                var nextCredential = new GmailCredential(config.ClientId, refresh.GetString()!, profile.RootElement.GetProperty("emailAddress").GetString()!);
                secrets.Write("gmail", ResearchStore.Serialize(nextCredential));
                credential = nextCredential; accessToken = nextAccess;
                credentialLoaded = true; CredentialError = null;
                expires = DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32() - 60);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { throw new InvalidOperationException("GMAIL_AUTH_TIMEOUT"); }
            finally { listener.Stop(); }
        }
        finally { gate.Release(); }
    }

    private static async Task<string> OAuthErrorAsync(HttpResponseMessage response, CancellationToken token)
    {
        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (body.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                return error.GetString() switch { "invalid_client" => "GMAIL_OAUTH_INVALID_CLIENT", "invalid_grant" => "GMAIL_OAUTH_INVALID_GRANT", "access_denied" => "GMAIL_AUTH_DENIED", _ => "GMAIL_AUTH_EXCHANGE_FAILED" };
        }
        catch (JsonException) { }
        return "GMAIL_AUTH_EXCHANGE_FAILED";
    }
    private static async Task<string> ProfileErrorAsync(HttpResponseMessage response, CancellationToken token)
    {
        var body = await response.Content.ReadAsStringAsync(token);
        return response.StatusCode == HttpStatusCode.Forbidden && (body.Contains("SERVICE_DISABLED", StringComparison.Ordinal) || body.Contains("accessNotConfigured", StringComparison.Ordinal))
            ? "GMAIL_API_DISABLED" : "GMAIL_PROFILE_FAILED";
    }

    private static async Task<string> ReceiveCodeAsync(TcpListener listener, string state, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var socket = await listener.AcceptTcpClientAsync(cancellationToken);
            using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectionTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            var token = connectionTimeout.Token;
            try
            {
                await using var stream = socket.GetStream();
                // Browsers may preconnect without sending a request. Bound each socket
                // and read the complete headers before closing to avoid a TCP reset.
                var header = new byte[16384];
                var length = 0;
                while (length < header.Length)
                {
                    if (await stream.ReadAsync(header.AsMemory(length, 1), token) == 0) break;
                    length++;
                    if (length >= 4 && header[length - 4] == 13 && header[length - 3] == 10 && header[length - 2] == 13 && header[length - 1] == 10) break;
                }
                var target = Encoding.ASCII.GetString(header, 0, length).Split("\r\n")[0].Split(' ');
                var valid = length < header.Length && target is { Length: 3 } && target[0] == "GET" && target[1].StartsWith("/?", StringComparison.Ordinal);
                var query = valid ? ParseQuery(target[1][2..]) : [];
                var supplied = query.GetValueOrDefault("state") ?? "";
                valid &= CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(supplied));
                var success = valid && query.TryGetValue("code", out var code) && !string.IsNullOrWhiteSpace(code);
                var body = success ? "Gmail authorization received. Return to Tools Touch to check the connection result." : "Authorization was not accepted. Return to Tools Touch.";
                var bytes = Encoding.UTF8.GetBytes(body);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {(success ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), token);
                await stream.WriteAsync(bytes, token);
                if (success) return query["code"];
                if (valid && query.ContainsKey("error")) throw new InvalidOperationException("GMAIL_AUTH_DENIED");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            catch (IOException) when (!cancellationToken.IsCancellationRequested) { }

        }
    }
    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>();
        foreach (var pair in query.Split('&'))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2 || !values.TryAdd(Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1].Replace('+', ' ')))) return [];
        }
        return values;
    }

    public async Task<string> AccessTokenAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            LoadCredential();
            if (accessToken != null && DateTimeOffset.UtcNow < expires) return accessToken;
            var current = credential ?? throw new InvalidOperationException("GMAIL_NOT_CONNECTED");
            var config = clientConfig();
            if (config.ClientId != current.ClientId) throw new InvalidOperationException("GMAIL_CLIENT_CHANGED_RECONNECT");
            using var response = await client.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["client_id"] = config.ClientId, ["client_secret"] = config.ClientSecret, ["refresh_token"] = current.RefreshToken, ["grant_type"] = "refresh_token" }), cancellationToken);
            if (!response.IsSuccessStatusCode) { accessToken = null; throw new InvalidOperationException(await OAuthErrorAsync(response, cancellationToken)); }
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            accessToken = json.RootElement.GetProperty("access_token").GetString()!;
            expires = DateTimeOffset.UtcNow.AddSeconds(json.RootElement.GetProperty("expires_in").GetInt32() - 60);
            if (json.RootElement.TryGetProperty("refresh_token", out var refresh))
            {
                credential = current with { RefreshToken = refresh.GetString()! };
                secrets.Write("gmail", ResearchStore.Serialize(credential));
            }
            return accessToken;
        }
        finally { gate.Release(); }
    }
}
