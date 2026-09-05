using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MimeKit;

namespace ToolsTouch.Core;

public sealed record GmailThreadStatus(string ThreadId, bool HasReply);

public sealed class GmailService(HttpClient client, IGmailAuthorization authorization) : IMailTransport
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

    public static byte[] BuildMime(SendSnapshot snapshot, string sender)
    {
        using var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(sender)); message.To.Add(MailboxAddress.Parse(snapshot.Recipient));
        message.Subject = snapshot.Subject; message.MessageId = snapshot.RfcMessageId.Trim('<', '>');
        message.Date = DateTimeOffset.UtcNow;
        var body = new BodyBuilder { TextBody = snapshot.Body };
        if (snapshot.AttachmentBytes != null)
            body.Attachments.Add(snapshot.AttachmentName ?? "CV.pdf", snapshot.AttachmentBytes, new ContentType("application", "pdf"));
        message.Body = body.ToMessageBody();
        using var output = new MemoryStream(); message.WriteTo(output); return output.ToArray();
    }

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
        var query = "in:sent rfc822msgid:" + snapshot.RfcMessageId.Trim('<', '>');
        var result = await GetAsync("messages?maxResults=20&q=" + Uri.EscapeDataString(query), cancellationToken);
        if (!result.TryGetProperty("messages", out var messages)) return null;
        foreach (var item in messages.EnumerateArray())
        {
            var message = await GetAsync("messages/" + Uri.EscapeDataString(item.GetProperty("id").GetString()!) + "?format=metadata&metadataHeaders=Message-ID&metadataHeaders=To", cancellationToken);
            if (!message.GetProperty("labelIds").EnumerateArray().Any(label => label.GetString() == "SENT")) continue;
            var headers = message.GetProperty("payload").GetProperty("headers").EnumerateArray().ToArray();
            var messageId = Header(headers, "Message-ID"); var recipient = Header(headers, "To");
            if (messageId.Trim('<', '>') == snapshot.RfcMessageId.Trim('<', '>') &&
                InternetAddressList.TryParse(recipient, out var recipients) && recipients.Mailboxes.Any(mailbox => mailbox.Address.Equals(snapshot.Recipient, StringComparison.OrdinalIgnoreCase)))
                return new(message.GetProperty("id").GetString()!, message.GetProperty("threadId").GetString()!);
        }
        return null; // Not found is not proof the send failed. Caller must keep Unknown.
    }

    public async Task<GmailThreadStatus> GetThreadStatusAsync(string threadId, string sentMessageId, CancellationToken cancellationToken)
    {
        var thread = await GetAsync("threads/" + Uri.EscapeDataString(threadId) + "?format=metadata&metadataHeaders=From", cancellationToken);
        var messages = thread.GetProperty("messages").EnumerateArray().ToArray();
        var sent = messages.FirstOrDefault(message => message.GetProperty("id").GetString() == sentMessageId);
        if (sent.ValueKind == JsonValueKind.Undefined) return new(threadId, false);
        var sentAt = long.Parse(sent.GetProperty("internalDate").GetString()!);
        var reply = messages.Any(message => message.GetProperty("id").GetString() != sentMessageId &&
            long.Parse(message.GetProperty("internalDate").GetString()!) >= sentAt &&
            !message.GetProperty("labelIds").EnumerateArray().Any(label => label.GetString() is "SENT" or "DRAFT") &&
            InternetAddressList.TryParse(Header(message.GetProperty("payload").GetProperty("headers").EnumerateArray().ToArray(), "From"), out var from) &&
            from.Mailboxes.Any(mailbox => !mailbox.Address.Equals(Account, StringComparison.OrdinalIgnoreCase)));
        return new(threadId, reply);
    }
    private static string Header(JsonElement[] headers, string name) => headers.FirstOrDefault(header => header.GetProperty("name").GetString()!.Equals(name, StringComparison.OrdinalIgnoreCase)) is var found && found.ValueKind != JsonValueKind.Undefined ? found.GetProperty("value").GetString() ?? "" : "";
}
