namespace ToolsTouch.Core;

public sealed record GmailMessageMetadata(string Id, string FromAddress, string? ToAddress, string? MessageId,
    string? InReplyTo, string ReferencesJson, DateTimeOffset? InternalDate, bool IsSelf, string RelationState);

public sealed record GmailThreadStatus(string ThreadId, bool HasReply, GmailMessageMetadata[]? Messages = null);

public sealed record GmailSentCandidate(string ProviderMessageId, string ThreadId);
