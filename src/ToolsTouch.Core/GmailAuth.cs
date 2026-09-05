using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        var installed = json.RootElement.GetProperty("installed");
        return new(installed.GetProperty("client_id").GetString()!, installed.GetProperty("client_secret").GetString()!);
    }
}
public sealed record GmailCredential(string ClientId, string RefreshToken, string Email);
public interface IGmailAuthorization
{
    string? Account { get; }
    Task<string> AccessTokenAsync(CancellationToken cancellationToken);
}

public sealed class GmailAuth(HttpClient client, ISecretStore secrets, Func<GoogleClient> clientConfig) : IGmailAuthorization
{
    public const string Scopes = "https://www.googleapis.com/auth/gmail.send https://www.googleapis.com/auth/gmail.readonly";
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? accessToken;
    private DateTimeOffset expires;
    private GmailCredential? credential = ReadCredential(secrets);
    public string? Account => credential?.Email;
    private static GmailCredential? ReadCredential(ISecretStore secrets) => secrets.Read("gmail") is { } json ? JsonSerializer.Deserialize<GmailCredential>(json, ResearchStore.Json) : null;
    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public async Task ConnectAsync(Action<Uri> openBrowser, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var config = clientConfig();
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
                openBrowser(new Uri("https://accounts.google.com/o/oauth2/v2/auth?" + string.Join("&", parameters.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)))));
                var code = await ReceiveCodeAsync(listener, state, token);
                using var response = await client.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = config.ClientId, ["client_secret"] = config.ClientSecret, ["code"] = code,
                    ["code_verifier"] = verifier, ["redirect_uri"] = redirect, ["grant_type"] = "authorization_code"
                }), token);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("GMAIL_AUTH_EXCHANGE_FAILED");
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                var root = json.RootElement;
                if (!root.TryGetProperty("refresh_token", out var refresh)) throw new InvalidOperationException("GMAIL_REFRESH_TOKEN_REQUIRED");
                var granted = root.TryGetProperty("scope", out var grantedScopes) ? grantedScopes.GetString()!.Split(' ') : [];
                if (Scopes.Split(' ').Except(granted).Any()) throw new InvalidOperationException("GMAIL_REQUIRED_SCOPES_MISSING");
                var nextAccess = root.GetProperty("access_token").GetString()!;
                using var profileRequest = new HttpRequestMessage(HttpMethod.Get, "https://gmail.googleapis.com/gmail/v1/users/me/profile");
                profileRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", nextAccess);
                using var profileResponse = await client.SendAsync(profileRequest, token);
                if (!profileResponse.IsSuccessStatusCode) throw new InvalidOperationException("GMAIL_PROFILE_FAILED");
                using var profile = JsonDocument.Parse(await profileResponse.Content.ReadAsStringAsync(token));
                var nextCredential = new GmailCredential(config.ClientId, refresh.GetString()!, profile.RootElement.GetProperty("emailAddress").GetString()!);
                secrets.Write("gmail", ResearchStore.Serialize(nextCredential));
                credential = nextCredential; accessToken = nextAccess;
                expires = DateTimeOffset.UtcNow.AddSeconds(root.GetProperty("expires_in").GetInt32() - 60);
            }
            finally { listener.Stop(); }
        }
        finally { gate.Release(); }
    }

    private static async Task<string> ReceiveCodeAsync(TcpListener listener, string state, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var socket = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = socket.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var line = await reader.ReadLineAsync(cancellationToken);
            var target = line?.Split(' ');
            var valid = target is { Length: 3 } && target[0] == "GET" && target[1].StartsWith("/?", StringComparison.Ordinal);
            var query = valid ? ParseQuery(target![1][2..]) : [];
            var supplied = query.GetValueOrDefault("state") ?? "";
            valid &= CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(supplied));
            var success = valid && query.TryGetValue("code", out var code) && !string.IsNullOrWhiteSpace(code);
            var body = success ? "Gmail authorization received. You can close this window." : "Authorization was not accepted. Return to Tools Touch.";
            var bytes = Encoding.UTF8.GetBytes(body);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {(success ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), cancellationToken);
            await stream.WriteAsync(bytes, cancellationToken);
            if (success) return query["code"];
            if (valid && query.ContainsKey("error")) throw new InvalidOperationException("GMAIL_AUTH_DENIED");
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
            if (accessToken != null && DateTimeOffset.UtcNow < expires) return accessToken;
            var current = credential ?? throw new InvalidOperationException("GMAIL_NOT_CONNECTED");
            var config = clientConfig();
            if (config.ClientId != current.ClientId) throw new InvalidOperationException("GMAIL_CLIENT_CHANGED_RECONNECT");
            using var response = await client.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["client_id"] = config.ClientId, ["client_secret"] = config.ClientSecret, ["refresh_token"] = current.RefreshToken, ["grant_type"] = "refresh_token" }), cancellationToken);
            if (!response.IsSuccessStatusCode) { accessToken = null; throw new InvalidOperationException("GMAIL_RECONNECT_REQUIRED"); }
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
