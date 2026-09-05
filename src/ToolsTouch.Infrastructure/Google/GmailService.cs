using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MimeKit;
using ToolsTouch.Application;

namespace ToolsTouch.Core;

public sealed class GmailService(HttpClient client, IGmailAccountPort authorization) : IMailTransport, ISendTransport
{
    public string? Account => authorization.Account;

    public async Task<SendReceipt> SendAsync(SendSnapshot snapshot, CancellationToken cancellationToken)
    {
        string token;
        byte[] mime;
        try
        {
            token = await authorization.AccessTokenAsync(cancellationToken);
            mime = BuildMime(snapshot, Account ?? throw new InvalidOperationException("GMAIL_NOT_CONNECTED"));
        }
        catch (Exception) { throw new SendRejectedException("GMAIL_PREFLIGHT_FAILED"); }
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://gmail.googleapis.com/gmail/v1/users/me/messages/send");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(ResearchStore.Serialize(new { raw = GmailAuth.Base64Url(mime) }), Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, cancellationToken);
        if ((int)response.StatusCode is >= 400 and < 500 && response.StatusCode != HttpStatusCode.RequestTimeout)
            throw new SendRejectedException("GMAIL_SEND_REJECTED_" + (int)response.StatusCode);
        response.EnsureSuccessStatusCode(); // 5xx, timeout or malformed success stays Unknown in OutreachService.
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return new(json.RootElement.GetProperty("id").GetString()!, json.RootElement.GetProperty("threadId").GetString()!);
    }

    public async Task<SendTransportReceipt> SendAsync(SendEnvelope envelope, CancellationToken cancellationToken)
    {
        if (!string.Equals(Account, envelope.SenderAccount, StringComparison.OrdinalIgnoreCase))
            throw new SendRejectedException("GMAIL_ACCOUNT_CHANGED");
        if (!Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(envelope.MimeBytes)).Equals(envelope.MimeHash, StringComparison.OrdinalIgnoreCase))
            throw new SendRejectedException("GMAIL_MIME_HASH_MISMATCH");
        string token;
        try { token = await authorization.AccessTokenAsync(cancellationToken); }
        catch (Exception) { throw new SendRejectedException("GMAIL_PREFLIGHT_FAILED"); }
        var request = new HttpRequestMessage(HttpMethod.Post, "https://gmail.googleapis.com/gmail/v1/users/me/messages/send");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(ResearchStore.Serialize(new { raw = GmailAuth.Base64Url(envelope.MimeBytes) }), Encoding.UTF8, "application/json");
        using (request)
        using (var response = await client.SendAsync(request, cancellationToken))
        {
            if ((int)response.StatusCode is >= 400 and < 500 && response.StatusCode != HttpStatusCode.RequestTimeout)
                throw new SendRejectedException("GMAIL_SEND_REJECTED_" + (int)response.StatusCode);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return new(json.RootElement.GetProperty("id").GetString()!, json.RootElement.GetProperty("threadId").GetString()!);
        }
    }

    public static byte[] BuildMime(SendSnapshot snapshot, string sender)
        => MimeComposer.Build(new MimeEnvelope(sender, snapshot.Recipient, snapshot.Subject, snapshot.Body,
            snapshot.RfcMessageId, DateTimeOffset.UtcNow, snapshot.AttachmentName, snapshot.AttachmentBytes));

    private async Task<JsonElement> GetAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        var token = await authorization.AccessTokenAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://gmail.googleapis.com/gmail/v1/users/me/" + relativeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.Clone();
    }

    public async Task<SendReceipt?> FindSentAsync(SendSnapshot snapshot, CancellationToken cancellationToken)
    {
        var candidates = await FindSentCandidatesAsync(snapshot, cancellationToken);
        return candidates.Count == 1 ? new(candidates[0].ProviderMessageId, candidates[0].ThreadId) : null;
    }

    public async Task<IReadOnlyList<GmailSentCandidate>> FindSentCandidatesAsync(SendSnapshot snapshot, CancellationToken cancellationToken)
    {
        var query = "in:sent rfc822msgid:" + snapshot.RfcMessageId.Trim('<', '>');
        var result = await GetAsync("messages?maxResults=20&q=" + Uri.EscapeDataString(query), cancellationToken);
        if (!result.TryGetProperty("messages", out var messages)) return [];
        var matches = new List<GmailSentCandidate>();
        foreach (var item in messages.EnumerateArray())
        {
            var message = await GetAsync("messages/" + Uri.EscapeDataString(item.GetProperty("id").GetString()!) + "?format=metadata&metadataHeaders=Message-ID&metadataHeaders=To&metadataHeaders=From", cancellationToken);
            if (!message.GetProperty("labelIds").EnumerateArray().Any(label => label.GetString() == "SENT")) continue;
            var headers = message.GetProperty("payload").GetProperty("headers").EnumerateArray().ToArray();
            var messageId = Header(headers, "Message-ID"); var recipient = Header(headers, "To");
            if (messageId.Trim('<', '>') == snapshot.RfcMessageId.Trim('<', '>') &&
                InternetAddressList.TryParse(recipient, out var recipients) && recipients.Mailboxes.Any(mailbox => mailbox.Address.Equals(snapshot.Recipient, StringComparison.OrdinalIgnoreCase)))
                matches.Add(new(message.GetProperty("id").GetString()!, message.GetProperty("threadId").GetString()!));
        }
        return matches;
    }

    public async Task<GmailThreadStatus> GetThreadStatusAsync(string threadId, string sentMessageId, CancellationToken cancellationToken)
    {
        var thread = await GetAsync("threads/" + Uri.EscapeDataString(threadId) + "?format=metadata&metadataHeaders=From&metadataHeaders=To&metadataHeaders=Message-ID&metadataHeaders=In-Reply-To&metadataHeaders=References", cancellationToken);
        var messages = thread.GetProperty("messages").EnumerateArray().ToArray();
        var parsed = messages.Select(message => ParseMessage(message)).ToArray();
        var sent = parsed.FirstOrDefault(message => message.Id == sentMessageId);
        if (sent is null) return new(threadId, false, parsed);
        var sentReferences = new[] { sent.MessageId }.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim('<', '>')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reply = parsed.Any(message => message.Id != sentMessageId && !message.IsSelf &&
            message.InternalDate >= sent.InternalDate && (message.RelationState == "Reply" ||
                References(message).Any(reference => sentReferences.Contains(reference))));
        return new(threadId, reply, parsed);
    }
    private GmailMessageMetadata ParseMessage(JsonElement message)
    {
        var headers = message.GetProperty("payload").GetProperty("headers").EnumerateArray().ToArray();
        var from = Header(headers, "From");
        var isSent = message.GetProperty("labelIds").EnumerateArray().Any(label => label.GetString() == "SENT");
        var isSelf = message.GetProperty("labelIds").EnumerateArray().Any(label => label.GetString() is "SENT" or "DRAFT") ||
            (InternetAddressList.TryParse(from, out var parsed) && parsed.Mailboxes.Any(mailbox => mailbox.Address.Equals(Account, StringComparison.OrdinalIgnoreCase)));
        var internalDate = message.TryGetProperty("internalDate", out var date) && long.TryParse(date.GetString(), out var milliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : (DateTimeOffset?)null;
        var relation = isSent ? "Sent" : isSelf ? "ThreadMember" :
            internalDate is null ? "Ambiguous" : "Reply";
        return new(message.GetProperty("id").GetString()!, from, HeaderOrNull(headers, "To"), HeaderOrNull(headers, "Message-ID"),
            HeaderOrNull(headers, "In-Reply-To"), JsonSerializer.Serialize(Header(headers, "References").Split(' ', StringSplitOptions.RemoveEmptyEntries)),
            internalDate, isSelf, relation);
    }
    private static string[] References(GmailMessageMetadata message)
    {
        try { return JsonSerializer.Deserialize<string[]>(message.ReferencesJson) ?? []; }
        catch (JsonException) { return []; }
    }
    private static string? HeaderOrNull(JsonElement[] headers, string name)
    {
        var value = Header(headers, name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
    private static string Header(JsonElement[] headers, string name) => headers.FirstOrDefault(header => header.GetProperty("name").GetString()!.Equals(name, StringComparison.OrdinalIgnoreCase)) is var found && found.ValueKind != JsonValueKind.Undefined ? found.GetProperty("value").GetString() ?? "" : "";
}
