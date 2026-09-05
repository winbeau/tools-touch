namespace ToolsTouch.Application;

public sealed record DeliveryCheckRecord(string Id, string DraftId, string? AttemptId, string SenderAccount,
    string RfcMessageId, string Outcome, string? ProviderMessageId, string? ThreadId, string? Error, DateTimeOffset CheckedAt);

public sealed record ReplyMessageRecord(string Id, string Account, string ThreadId, string ProviderMessageId,
    string? OutreachId, string FromAddress, string? ToAddress, string? InReplyTo, string ReferencesJson,
    bool IsSelf, string RelationState, DateTimeOffset? InternalDate, DateTimeOffset RecordedAt);
