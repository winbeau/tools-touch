using ToolsTouch.Core;

namespace ToolsTouch.Application;

public sealed record SendPreview(
    string ConfirmationId,
    string DraftId,
    string ProfessorId,
    int DraftVersion,
    int DraftRevision,
    string SenderAccount,
    string Recipient,
    string Subject,
    string Body,
    string? AttachmentName,
    long AttachmentBytes,
    string? ProfileId,
    string SnapshotHash,
    string MimeArtifactId,
    DateTimeOffset ExpiresAt);

public sealed record SendAttemptRecord(
    string Id,
    string DraftId,
    string ConfirmationId,
    int DraftRevision,
    string SenderAccount,
    string RfcMessageId,
    string MimeArtifactId,
    string MimeHash,
    string SnapshotHash,
    string State,
    string? ProviderMessageId,
    string? ThreadId,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt);

public sealed record SendEnvelope(
    string AttemptId,
    string SenderAccount,
    string RfcMessageId,
    string MimeArtifactId,
    string MimeHash,
    byte[] MimeBytes);

public sealed record SendTransportReceipt(string MessageId, string ThreadId);

public interface ISendTransport
{
    string? Account { get; }
    Task<SendTransportReceipt> SendAsync(SendEnvelope envelope, CancellationToken cancellationToken);
}

public interface ISendCoordinator
{
    SendPreview Prepare(string draftId, string senderAccount, TimeSpan? lifetime = null);
    SendAttemptRecord Confirm(string confirmationId, string senderAccount);
    Task<SendAttemptRecord> SendAsync(string attemptId, ISendTransport transport, CancellationToken cancellationToken = default);
    void RecoverInterruptedSends();
}
