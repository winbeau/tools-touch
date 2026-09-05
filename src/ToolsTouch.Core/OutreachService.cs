using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;

namespace ToolsTouch.Core;

public sealed record Draft(string Id, string ProfessorId, int Version, string Recipient,
    string Subject, string Body, string? CvPath, string? CvHash, string State, int Revision = 1);
public sealed record SendSnapshot(string DraftId, string Recipient, string Subject, string Body,
    string? AttachmentName, byte[]? AttachmentBytes, string? AttachmentHash, string RfcMessageId);
public sealed record SendReceipt(string MessageId, string ThreadId);
public sealed record OutreachHistory(string Id, int Version, string Subject, string State, string CreatedAt, string ReplyState);

// Only an explicitly rejected request is safe to mark Failed. Transport errors are ambiguous.
public sealed class SendRejectedException(string message) : Exception(message);
public interface IMailTransport
{
    Task<SendReceipt> SendAsync(SendSnapshot snapshot, CancellationToken cancellationToken);
}

public sealed class OutreachService(LocalDatabase database)
{
    public Draft? FindByIdempotencyKey(string key)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "SELECT Id FROM Outreach WHERE IdempotencyKey=$key", ("$key", key));
        return command.ExecuteScalar() is string id ? Get(id) : null;
    }
    public IReadOnlyList<OutreachHistory> History(string professorId)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            SELECT o.Id,o.Version,o.Subject,o.State,o.CreatedAt,COALESCE(t.ReplyState,'NoReply')
            FROM Outreach o LEFT JOIN EmailThread t ON t.Account=o.SenderAccount AND t.ThreadId=o.ThreadId
            WHERE o.ProfessorId=$prof ORDER BY o.Version DESC
            """, ("$prof", professorId));
        using var reader = command.ExecuteReader();
        var result = new List<OutreachHistory>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        return result;
    }
    public Draft CreateDraft(string professorId, string recipient, string subject, string body,
        string? cvPath, string idempotencyKey, string evidenceJson = "[]", string? profileId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var existing = LocalDatabase.Command(connection,
            "SELECT Id FROM Outreach WHERE IdempotencyKey=$key", ("$key", idempotencyKey));
        existing.Transaction = transaction;
        if (existing.ExecuteScalar() is string existingId)
        {
            transaction.Commit();
            var saved = Get(existingId);
            if (saved.ProfessorId != professorId || saved.Recipient != recipient ||
                saved.Subject != subject || saved.Body != body || saved.CvPath != cvPath)
                throw new InvalidOperationException("IDEMPOTENCY_CONFLICT");
            return saved;
        }
        var id = Guid.NewGuid().ToString("N");
        string? hash = cvPath is null ? null : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(cvPath)));
        using var insert = LocalDatabase.Command(connection, """
            INSERT INTO Outreach(Id,ProfessorId,Version,Recipient,Subject,Body,CvPath,CvHash,State,IdempotencyKey,CreatedAt,EvidenceJson,ProfileId)
            SELECT $id,$prof,COALESCE(MAX(Version),0)+1,$to,$subject,$body,$cv,$hash,'Draft',$key,$now,$evidence,$profile
            FROM Outreach WHERE ProfessorId=$prof
            """, ("$id", id), ("$prof", professorId), ("$to", recipient), ("$subject", subject),
            ("$body", body), ("$cv", cvPath), ("$hash", hash), ("$key", idempotencyKey),
            ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$evidence", evidenceJson), ("$profile", profileId));
        insert.Transaction = transaction;
        insert.ExecuteNonQuery();
        transaction.Commit();
        return Get(id);
    }

    public Draft Get(string id)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection,
            "SELECT Id,ProfessorId,Version,Recipient,Subject,Body,CvPath,CvHash,State,Revision FROM Outreach WHERE Id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("DRAFT_NOT_FOUND");
        return new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8), reader.GetInt32(9));
    }

    public IReadOnlyList<Draft> List(string? professorId = null)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection,
            "SELECT Id FROM Outreach WHERE $prof IS NULL OR ProfessorId=$prof ORDER BY CreatedAt DESC", ("$prof", professorId));
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read()) ids.Add(reader.GetString(0));
        return ids.Select(Get).ToArray();
    }

    public Draft Edit(string id, int expectedRevision, string recipient, string subject, string body, string? cvPath)
    {
        var hash = cvPath == null ? null : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(cvPath)));
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            UPDATE Outreach SET Recipient=$to,Subject=$subject,Body=$body,CvPath=$cv,CvHash=$hash,
            Revision=Revision+1,State='Draft',Error=NULL,SnapshotJson=NULL
            WHERE Id=$id AND Revision=$revision AND State IN ('Draft','Failed')
            """, ("$to", recipient), ("$subject", subject), ("$body", body), ("$cv", cvPath), ("$hash", hash), ("$id", id), ("$revision", expectedRevision));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("DRAFT_CHANGED_OR_LOCKED");
        return Get(id);
    }

    // Called only by the desktop's explicit Send action, never by the agent tool dispatcher.
    public async Task<SendReceipt> SendAsync(string id, IMailTransport transport, CancellationToken cancellationToken = default, string? senderAccount = null, int? expectedRevision = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var draft = Get(id);
        if (expectedRevision != null && draft.Revision != expectedRevision) throw new InvalidOperationException("DRAFT_CHANGED_REVIEW_REQUIRED");
        if (draft.State is not ("Draft" or "Failed")) throw new InvalidOperationException("DRAFT_NOT_SENDABLE");
        if (!MailAddress.TryCreate(draft.Recipient, out var address) || address.Address != draft.Recipient ||
            draft.Recipient.Contains('\r') || draft.Recipient.Contains('\n'))
            throw new InvalidOperationException("INVALID_RECIPIENT");
        if (string.IsNullOrWhiteSpace(draft.Subject) || draft.Subject.Contains('\r') || draft.Subject.Contains('\n') ||
            string.IsNullOrWhiteSpace(draft.Body)) throw new InvalidOperationException("INVALID_CONTENT");
        byte[]? attachment = draft.CvPath is null ? null : await File.ReadAllBytesAsync(draft.CvPath, cancellationToken);
        var hash = attachment is null ? null : Convert.ToHexString(SHA256.HashData(attachment));
        if (hash != draft.CvHash) throw new InvalidOperationException("CV_CHANGED_REVIEW_REQUIRED");
        var snapshot = new SendSnapshot(id, draft.Recipient, draft.Subject, draft.Body,
            draft.CvPath is null ? null : Path.GetFileName(draft.CvPath), attachment, hash,
            $"<{Guid.NewGuid():N}@tools-touch.local>");
        using (var connection = database.Open())
        using (var claim = LocalDatabase.Command(connection, """
            UPDATE Outreach SET State='Sending',SnapshotJson=$snapshot,Error=NULL,SenderAccount=$account
            WHERE Id=$id AND Revision=$revision AND State IN ('Draft','Failed')
            """, ("$snapshot", JsonSerializer.Serialize(snapshot)), ("$id", id), ("$revision", draft.Revision), ("$account", senderAccount)))
        {
            if (claim.ExecuteNonQuery() != 1) throw new InvalidOperationException("DRAFT_NOT_SENDABLE");
        }
        try
        {
            var receipt = await transport.SendAsync(snapshot, cancellationToken);
            using var connection = database.Open();
            using var complete = LocalDatabase.Command(connection,
                "UPDATE Outreach SET State='Sent',MessageId=$message,ThreadId=$thread WHERE Id=$id AND State='Sending'",
                ("$message", receipt.MessageId), ("$thread", receipt.ThreadId), ("$id", id));
            if (complete.ExecuteNonQuery() != 1) throw new InvalidOperationException("SEND_STATE_CONFLICT");
            return receipt;
        }
        catch (Exception exception)
        {
            using var connection = database.Open();
            using var fail = LocalDatabase.Command(connection,
                "UPDATE Outreach SET State=$state,Error=$error WHERE Id=$id AND State='Sending'",
                ("$state", exception is SendRejectedException ? "Failed" : "Unknown"),
                ("$error", exception.GetType().Name), ("$id", id));
            fail.ExecuteNonQuery();
            throw;
        }
    }

    // Invoke once at desktop startup, before accepting new work.
    public void RecoverInterruptedSends()
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection,
            "UPDATE Outreach SET State='Unknown',Error='PROCESS_INTERRUPTED' WHERE State='Sending'");
        command.ExecuteNonQuery();
    }

    public async Task<bool> ReconcileAsync(string id, GmailService gmail, CancellationToken cancellationToken = default)
    {
        using var connection = database.Open();
        using var lookup = LocalDatabase.Command(connection, "SELECT SnapshotJson,SenderAccount FROM Outreach WHERE Id=$id AND State='Unknown'", ("$id", id));
        SendSnapshot snapshot;
        using (var reader = lookup.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("SEND_NOT_UNKNOWN");
            if (reader.IsDBNull(1) || reader.GetString(1) != gmail.Account) throw new InvalidOperationException("GMAIL_ACCOUNT_MISMATCH");
            snapshot = JsonSerializer.Deserialize<SendSnapshot>(reader.GetString(0))!;
        }
        var receipt = await gmail.FindSentAsync(snapshot, cancellationToken);
        if (receipt == null) return false;
        using var update = LocalDatabase.Command(connection,
            "UPDATE Outreach SET State='Sent',MessageId=$message,ThreadId=$thread,Error=NULL WHERE Id=$id AND State='Unknown'",
            ("$message", receipt.MessageId), ("$thread", receipt.ThreadId), ("$id", id));
        update.ExecuteNonQuery(); return true;
    }

    public async Task<int> SyncRepliesAsync(GmailService gmail, CancellationToken cancellationToken = default)
    {
        using var connection = database.Open();
        using var query = LocalDatabase.Command(connection,
            "SELECT Id,MessageId,ThreadId FROM Outreach WHERE State='Sent' AND SenderAccount=$account", ("$account", gmail.Account));
        var sent = new List<(string Id, string Message, string Thread)>();
        using (var reader = query.ExecuteReader()) while (reader.Read()) sent.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        var replies = 0;
        foreach (var item in sent)
        {
            var thread = await gmail.GetThreadStatusAsync(item.Thread, item.Message, cancellationToken);
            using var save = LocalDatabase.Command(connection, """
                INSERT INTO EmailThread(Account,ThreadId,OutreachId,ReplyState,SyncedAt) VALUES($account,$thread,$id,$state,$now)
                ON CONFLICT(Account,ThreadId) DO UPDATE SET ReplyState=excluded.ReplyState,SyncedAt=excluded.SyncedAt
                """, ("$account", gmail.Account), ("$thread", item.Thread), ("$id", item.Id),
                ("$state", thread.HasReply ? "Replied" : "NoReply"), ("$now", DateTimeOffset.UtcNow.ToString("O")));
            save.ExecuteNonQuery(); if (thread.HasReply) replies++;
        }
        return replies;
    }
    public int ReplyCount()
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "SELECT COUNT(*) FROM EmailThread WHERE ReplyState='Replied'");
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
