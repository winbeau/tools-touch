using MimeKit;

namespace ToolsTouch.Core;

public sealed record MimeEnvelope(string Sender, string Recipient, string Subject, string Body,
    string RfcMessageId, DateTimeOffset Date, string? AttachmentName = null, byte[]? AttachmentBytes = null);

public static class MimeComposer
{
    public static byte[] Build(MimeEnvelope input)
    {
        using var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(input.Sender));
        message.To.Add(MailboxAddress.Parse(input.Recipient));
        message.Subject = input.Subject;
        message.MessageId = input.RfcMessageId.Trim('<', '>');
        message.Date = input.Date;
        var body = new BodyBuilder { TextBody = input.Body };
        if (input.AttachmentBytes is not null)
            body.Attachments.Add(input.AttachmentName ?? "CV.pdf", input.AttachmentBytes, new ContentType("application", "pdf"));
        message.Body = body.ToMessageBody();
        using var output = new MemoryStream();
        message.WriteTo(output);
        return output.ToArray();
    }
}
