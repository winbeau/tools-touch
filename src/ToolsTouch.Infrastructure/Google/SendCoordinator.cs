using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MimeKit;
using ToolsTouch.Application;

namespace ToolsTouch.Core;

public sealed class SendCoordinator(LocalDatabase database, OutreachService outreach, ArtifactStore artifacts,
    Func<DateTimeOffset>? clock = null) : ISendCoordinator
{
    private const int MaxMimeBytes = 20 * 1024 * 1024;
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    public SendPreview Prepare(string draftId, string senderAccount, TimeSpan? lifetime = null)
    {
        if (string.IsNullOrWhiteSpace(senderAccount) || !MailAddress.TryCreate(senderAccount, out var sender) || sender.Address != senderAccount)
            throw new InvalidOperationException("INVALID_SENDER_ACCOUNT");
        var ttl = lifetime ?? TimeSpan.FromMinutes(15);
        if (ttl <= TimeSpan.Zero || ttl > TimeSpan.FromHours(24)) throw new InvalidOperationException("INVALID_CONFIRMATION_LIFETIME");
        var draft = outreach.Get(draftId);
        ValidateDraft(draft);
        EnsureNoPendingAttempt(draft.Id);
        var attachment = ReadAttachment(draft);
        var createdAt = now();
        var rfcMessageId = $"<{Guid.NewGuid():N}@tools-touch.local>";
        var mime = MimeComposer.Build(new MimeEnvelope(sender.Address, draft.Recipient, draft.Subject, draft.Body,
            rfcMessageId, createdAt, attachment.Name, attachment.Bytes));
        if (mime.Length > MaxMimeBytes) throw new InvalidOperationException("MIME_TOO_LARGE");
        var artifact = artifacts.Stage("send-mime", mime, "message/rfc822", ".eml");
        var expiresAt = createdAt.Add(ttl);
        var snapshotHash = HashSnapshot(new
        {
            draftId = draft.Id, professorId = draft.ProfessorId, version = draft.Version, revision = draft.Revision,
            senderAccount = sender.Address, recipient = draft.Recipient, cc = "[]", bcc = "[]", draft.Subject,
            draft.Body, cvPath = draft.CvPath, cvHash = draft.CvHash, attachmentName = attachment.Name,
            attachmentBytes = attachment.Bytes?.LongLength ?? 0, profileId = draft.ProfileId, mimeArtifactId = artifact.Id,
            mimeHash = artifact.ContentHash, rfcMessageId
        });
        var confirmationId = Guid.NewGuid().ToString("N");
        var createdText = createdAt.ToString("O");
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using (var pending = LocalDatabase.Command(connection,
            "SELECT COUNT(*) FROM SendAttempt WHERE DraftId=$draft AND State IN ('Sending','Transmitting','Unknown')", ("$draft", draft.Id)))
        {
            pending.Transaction = transaction;
            if (Convert.ToInt32(pending.ExecuteScalar()) != 0) throw new InvalidOperationException("SEND_ATTEMPT_PENDING");
        }
        ArtifactStore.Insert(connection, transaction, artifact);
        using (var insert = LocalDatabase.Command(connection, """
            INSERT INTO SendConfirmation(Id,DraftId,DraftVersion,DraftRevision,SenderAccount,Recipient,CcJson,BccJson,Subject,Body,CvPath,CvHash,AttachmentName,AttachmentBytes,ProfileId,SnapshotHash,MimeArtifactId,ExpiresAt,CreatedAt)
            VALUES($id,$draft,$version,$revision,$sender,$recipient,'[]','[]',$subject,$body,$cv,$cvhash,$attachment,$bytes,$profile,$snapshot,$mime,$expires,$created)
            """, ("$id", confirmationId), ("$draft", draft.Id), ("$version", draft.Version), ("$revision", draft.Revision),
            ("$sender", sender.Address), ("$recipient", draft.Recipient), ("$subject", draft.Subject), ("$body", draft.Body),
            ("$cv", draft.CvPath), ("$cvhash", draft.CvHash), ("$attachment", attachment.Name), ("$bytes", attachment.Bytes?.LongLength ?? 0),
            ("$profile", draft.ProfileId), ("$snapshot", snapshotHash), ("$mime", artifact.Id), ("$expires", expiresAt.ToString("O")), ("$created", createdText)))
        {
            insert.Transaction = transaction;
            insert.ExecuteNonQuery();
        }
        transaction.Commit();
        return new(confirmationId, draft.Id, draft.ProfessorId, draft.Version, draft.Revision, sender.Address,
            draft.Recipient, draft.Subject, draft.Body, attachment.Name, attachment.Bytes?.LongLength ?? 0,
            draft.ProfileId, snapshotHash, artifact.Id, expiresAt);
    }

    private void EnsureNoPendingAttempt(string draftId)
    {
        using var connection = database.Open();
        using var pending = LocalDatabase.Command(connection,
            "SELECT COUNT(*) FROM SendAttempt WHERE DraftId=$draft AND State IN ('Sending','Transmitting','Unknown')", ("$draft", draftId));
        if (Convert.ToInt32(pending.ExecuteScalar()) != 0) throw new InvalidOperationException("SEND_ATTEMPT_PENDING");
    }

    public SendAttemptRecord Confirm(string confirmationId, string senderAccount)
    {
        if (string.IsNullOrWhiteSpace(senderAccount)) throw new InvalidOperationException("INVALID_SENDER_ACCOUNT");
        var current = now();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        ConfirmationRow confirmation;
        using (var query = LocalDatabase.Command(connection, """
            SELECT c.Id,c.DraftId,c.DraftVersion,c.DraftRevision,c.SenderAccount,c.Recipient,c.Subject,c.Body,c.CvPath,c.CvHash,c.AttachmentName,c.AttachmentBytes,c.ProfileId,c.SnapshotHash,c.MimeArtifactId,c.ExpiresAt,c.ConsumedAt,
                   d.ProfessorId,d.Version,d.Revision,d.State,d.Recipient,d.Subject,d.Body,d.CvPath,d.CvHash,d.ProfileId
            FROM SendConfirmation c JOIN Outreach d ON d.Id=c.DraftId WHERE c.Id=$id
            """, ("$id", confirmationId)))
        {
            query.Transaction = transaction;
            using var reader = query.ExecuteReader();
            if (!reader.Read()) throw new KeyNotFoundException("SEND_CONFIRMATION_NOT_FOUND");
            confirmation = ConfirmationRow.Read(reader);
        }
        if (confirmation.ConsumedAt is not null) throw new InvalidOperationException("SEND_CONFIRMATION_CONSUMED");
        if (ParseTime(confirmation.ExpiresAt) <= current) throw new InvalidOperationException("SEND_CONFIRMATION_EXPIRED");
        if (!string.Equals(confirmation.SenderAccount, senderAccount, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SEND_ACCOUNT_CHANGED");
        if (confirmation.DraftState is not ("Draft" or "Failed") || confirmation.DraftRevision != confirmation.CurrentRevision ||
            confirmation.DraftVersion != confirmation.CurrentVersion || confirmation.Recipient != confirmation.CurrentRecipient ||
            confirmation.Subject != confirmation.CurrentSubject || confirmation.Body != confirmation.CurrentBody ||
            confirmation.CvPath != confirmation.CurrentCvPath || confirmation.CvHash != confirmation.CurrentCvHash ||
            confirmation.ProfileId != confirmation.CurrentProfileId)
            throw new InvalidOperationException("SEND_SNAPSHOT_STALE");
        if (confirmation.CvPath is not null &&
            (!File.Exists(confirmation.CvPath) || !string.Equals(HashFile(confirmation.CvPath), confirmation.CvHash, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("SEND_SNAPSHOT_STALE");

        var attemptId = Guid.NewGuid().ToString("N");
        var rfcMessageId = $"<{Guid.NewGuid():N}@tools-touch.local>";
        // The RFC Message-ID is fixed in the MIME artifact; this should always be the one
        // captured during Prepare. Read it from the artifact only after the row is claimed.
        var artifact = ReadArtifact(connection, transaction, confirmation.MimeArtifactId);
        var mimeBytes = artifacts.ReadBytes(artifact);
        var messageId = ExtractMessageId(mimeBytes);
        if (messageId is null) throw new InvalidOperationException("SEND_ARTIFACT_INVALID");
        rfcMessageId = messageId;
        var sendSnapshot = JsonSerializer.Serialize(new SendSnapshot(confirmation.DraftId, confirmation.Recipient,
            confirmation.Subject, confirmation.Body, confirmation.AttachmentName, null, confirmation.CvHash, rfcMessageId));
        try
        {
            using var insert = LocalDatabase.Command(connection, """
                INSERT INTO SendAttempt(Id,DraftId,ConfirmationId,DraftRevision,SenderAccount,RfcMessageId,MimeArtifactId,MimeHash,SnapshotHash,State,CreatedAt,StartedAt)
                VALUES($id,$draft,$confirmation,$revision,$sender,$rfc,$mime,$mimehash,$snapshot,'Sending',$created,$started)
                """, ("$id", attemptId), ("$draft", confirmation.DraftId), ("$confirmation", confirmation.Id), ("$revision", confirmation.DraftRevision),
                ("$sender", confirmation.SenderAccount), ("$rfc", rfcMessageId), ("$mime", confirmation.MimeArtifactId),
                ("$mimehash", artifact.ContentHash), ("$snapshot", confirmation.SnapshotHash), ("$created", current.ToString("O")), ("$started", current.ToString("O")));
            insert.Transaction = transaction;
            insert.ExecuteNonQuery();
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("SEND_ATTEMPT_PENDING", error);
        }
        using (var consume = LocalDatabase.Command(connection,
            "UPDATE SendConfirmation SET ConsumedAt=$at WHERE Id=$id AND ConsumedAt IS NULL", ("$at", current.ToString("O")), ("$id", confirmation.Id)))
        {
            consume.Transaction = transaction;
            if (consume.ExecuteNonQuery() != 1) throw new InvalidOperationException("SEND_CONFIRMATION_CONSUMED");
        }
        using (var update = LocalDatabase.Command(connection, """
            UPDATE Outreach SET State='Sending',SnapshotJson=$snapshot,Error=NULL,SenderAccount=$sender
            WHERE Id=$id AND Revision=$revision AND State IN ('Draft','Failed')
            """, ("$snapshot", sendSnapshot), ("$sender", confirmation.SenderAccount),
            ("$id", confirmation.DraftId), ("$revision", confirmation.DraftRevision)))
        {
            update.Transaction = transaction;
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("SEND_SNAPSHOT_STALE");
        }
        transaction.Commit();
        return GetAttempt(attemptId);
    }

    public async Task<SendAttemptRecord> SendAsync(string attemptId, ISendTransport transport, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attempt = GetAttempt(attemptId);
        if (attempt.State != "Sending") throw new InvalidOperationException("SEND_ATTEMPT_NOT_SENDABLE");
        if (!string.Equals(transport.Account, attempt.SenderAccount, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SEND_ACCOUNT_CHANGED");
        var artifact = GetArtifact(attempt.MimeArtifactId);
        var mime = artifacts.ReadBytes(artifact);
        if (!artifact.ContentHash.Equals(attempt.MimeHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("SEND_ARTIFACT_INVALID");
        using (var connection = database.Open())
        using (var claim = LocalDatabase.Command(connection,
            "UPDATE SendAttempt SET State='Transmitting',StartedAt=$started WHERE Id=$id AND State='Sending'",
            ("$started", now().ToString("O")), ("$id", attemptId)))
        {
            if (claim.ExecuteNonQuery() != 1) throw new InvalidOperationException("SEND_ATTEMPT_NOT_SENDABLE");
        }
        try
        {
            var receipt = await transport.SendAsync(new SendEnvelope(attempt.Id, attempt.SenderAccount, attempt.RfcMessageId,
                attempt.MimeArtifactId, attempt.MimeHash, mime), cancellationToken);
            using var connection = database.Open();
            using var transaction = connection.BeginTransaction();
            using (var complete = LocalDatabase.Command(connection, """
                UPDATE SendAttempt SET State='Sent',ProviderMessageId=$message,ThreadId=$thread,Error=NULL,FinishedAt=$finished
                WHERE Id=$id AND State='Transmitting'
                """, ("$message", receipt.MessageId), ("$thread", receipt.ThreadId), ("$finished", now().ToString("O")), ("$id", attemptId)))
            {
                complete.Transaction = transaction;
                if (complete.ExecuteNonQuery() != 1) throw new InvalidOperationException("SEND_STATE_CONFLICT");
            }
            using (var draft = LocalDatabase.Command(connection,
                "UPDATE Outreach SET State='Sent',MessageId=$message,ThreadId=$thread,Error=NULL WHERE Id=$id AND State='Sending'",
                ("$message", receipt.MessageId), ("$thread", receipt.ThreadId), ("$id", attempt.DraftId)))
            { draft.Transaction = transaction; if (draft.ExecuteNonQuery() != 1) throw new InvalidOperationException("SEND_STATE_CONFLICT"); }
            transaction.Commit();
            return GetAttempt(attemptId);
        }
        catch (Exception error)
        {
            var state = error is SendRejectedException ? "Failed" : "Unknown";
            using var connection = database.Open();
            using var transaction = connection.BeginTransaction();
            using (var fail = LocalDatabase.Command(connection,
                "UPDATE SendAttempt SET State=$state,Error=$error,FinishedAt=$finished WHERE Id=$id AND State='Transmitting'",
                ("$state", state), ("$error", error.GetType().Name), ("$finished", now().ToString("O")), ("$id", attemptId)))
            { fail.Transaction = transaction; fail.ExecuteNonQuery(); }
            using (var draft = LocalDatabase.Command(connection,
                "UPDATE Outreach SET State=$state,Error=$error WHERE Id=$id AND State='Sending'",
                ("$state", state), ("$error", error.GetType().Name), ("$id", attempt.DraftId)))
            { draft.Transaction = transaction; draft.ExecuteNonQuery(); }
            transaction.Commit();
            throw;
        }
    }

    public void RecoverInterruptedSends()
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using (var draft = LocalDatabase.Command(connection,
            "UPDATE Outreach SET State='Unknown',Error='PROCESS_INTERRUPTED' WHERE State='Sending' AND Id IN (SELECT DraftId FROM SendAttempt WHERE State IN ('Sending','Transmitting'))"))
        { draft.Transaction = transaction; draft.ExecuteNonQuery(); }
        using (var attempt = LocalDatabase.Command(connection,
            "UPDATE SendAttempt SET State='Unknown',Error='PROCESS_INTERRUPTED',FinishedAt=$finished WHERE State IN ('Sending','Transmitting')",
            ("$finished", now().ToString("O"))))
        { attempt.Transaction = transaction; attempt.ExecuteNonQuery(); }
        transaction.Commit();
    }

    public SendAttemptRecord GetAttempt(string id)
    {
        using var connection = database.Open();
        return ReadAttempt(connection, null, id);
    }

    private SendAttemptRecord ReadAttempt(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using var command = LocalDatabase.Command(connection, "SELECT Id,DraftId,ConfirmationId,DraftRevision,SenderAccount,RfcMessageId,MimeArtifactId,MimeHash,SnapshotHash,State,ProviderMessageId,ThreadId,Error,CreatedAt,StartedAt,FinishedAt FROM SendAttempt WHERE Id=$id", ("$id", id));
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("SEND_ATTEMPT_NOT_FOUND");
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12), ParseTime(reader.GetString(13)),
            reader.IsDBNull(14) ? null : ParseTime(reader.GetString(14)), reader.IsDBNull(15) ? null : ParseTime(reader.GetString(15)));
    }

    private Artifact GetArtifact(string id)
    {
        using var connection = database.Open();
        return ReadArtifact(connection, null, id);
    }

    private static Artifact ReadArtifact(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using var command = LocalDatabase.Command(connection, "SELECT Id,Kind,RelativePath,ContentHash,ByteLength,MimeType,CreatedAt FROM Artifact WHERE Id=$id", ("$id", id));
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("SEND_ARTIFACT_NOT_FOUND");
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5), ParseTime(reader.GetString(6)));
    }

    private sealed record ConfirmationRow(string Id, string DraftId, int DraftVersion, int DraftRevision, string SenderAccount,
        string Recipient, string Subject, string Body, string? CvPath, string? CvHash, string? AttachmentName, long AttachmentBytes,
        string? ProfileId, string SnapshotHash, string MimeArtifactId, string ExpiresAt, string? ConsumedAt, string ProfessorId,
        int CurrentVersion, int CurrentRevision, string DraftState, string CurrentRecipient, string CurrentSubject, string CurrentBody,
        string? CurrentCvPath, string? CurrentCvHash, string? CurrentProfileId)
    {
        public static ConfirmationRow Read(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetString(4),
            reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10), reader.GetInt64(11), reader.IsDBNull(12) ? null : reader.GetString(12), reader.GetString(13), reader.GetString(14),
            reader.GetString(15), reader.IsDBNull(16) ? null : reader.GetString(16), reader.GetString(17), reader.GetInt32(18), reader.GetInt32(19), reader.GetString(20),
            reader.GetString(21), reader.GetString(22), reader.GetString(23), reader.IsDBNull(24) ? null : reader.GetString(24), reader.IsDBNull(25) ? null : reader.GetString(25),
            reader.IsDBNull(26) ? null : reader.GetString(26));
    }

    private static void ValidateDraft(Draft draft)
    {
        if (draft.State is not ("Draft" or "Failed")) throw new InvalidOperationException("DRAFT_NOT_SENDABLE");
        if (!MailAddress.TryCreate(draft.Recipient, out var recipient) || recipient.Address != draft.Recipient || draft.Recipient.Contains('\r') || draft.Recipient.Contains('\n'))
            throw new InvalidOperationException("INVALID_RECIPIENT");
        if (string.IsNullOrWhiteSpace(draft.Subject) || draft.Subject.Contains('\r') || draft.Subject.Contains('\n') || string.IsNullOrWhiteSpace(draft.Body))
            throw new InvalidOperationException("INVALID_CONTENT");
    }

    private static (string? Name, byte[]? Bytes) ReadAttachment(Draft draft)
    {
        if (draft.CvPath is null) return (null, null);
        if (!File.Exists(draft.CvPath)) throw new InvalidOperationException("CV_CHANGED_REVIEW_REQUIRED");
        var bytes = File.ReadAllBytes(draft.CvPath);
        if (draft.CvHash is null || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(draft.CvHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("CV_CHANGED_REVIEW_REQUIRED");
        return (Path.GetFileName(draft.CvPath), bytes);
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static DateTimeOffset ParseTime(string value) => DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);
    private static string HashSnapshot(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();
    private static string? ExtractMessageId(byte[] mime)
    {
        try
        {
            using var stream = new MemoryStream(mime, writable: false);
            using var message = MimeMessage.Load(stream);
            if (string.IsNullOrWhiteSpace(message.MessageId)) return null;
            return message.MessageId.StartsWith('<') ? message.MessageId : '<' + message.MessageId + '>';
        }
        catch (Exception) { return null; }
    }
}
