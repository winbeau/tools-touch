using System.Net.Mail;
using System.Security.Cryptography;
using System.Text.Json;
using ToolsTouch.Application;

namespace ToolsTouch.Core;

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
        string? cvPath, string idempotencyKey, string evidenceJson = "[]", string? profileId = null,
        string? appointmentId = null, string? preferenceId = null, string? analysisArtifactId = null,
        string language = "zh-CN", string promptVersion = "legacy", string? model = null, string draftRequestJson = "{}")
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
                saved.Subject != subject || saved.Body != body || saved.CvPath != cvPath || saved.EvidenceJson != evidenceJson ||
                saved.ProfileId != profileId || saved.AppointmentId != appointmentId || saved.PreferenceId != preferenceId ||
                saved.AnalysisArtifactId != analysisArtifactId || saved.Language != language || saved.PromptVersion != promptVersion ||
                saved.Model != model || saved.DraftRequestJson != draftRequestJson)
                throw new InvalidOperationException("IDEMPOTENCY_CONFLICT");
            return saved;
        }
        var id = Guid.NewGuid().ToString("N");
        string? hash = cvPath is null ? null : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(cvPath)));
        using var insert = LocalDatabase.Command(connection, """
            INSERT INTO Outreach(Id,ProfessorId,Version,Recipient,Subject,Body,CvPath,CvHash,State,IdempotencyKey,CreatedAt,EvidenceJson,ProfileId,AppointmentId,PreferenceId,AnalysisArtifactId,Language,PromptVersion,Model,DraftRequestJson)
            SELECT $id,$prof,COALESCE(MAX(Version),0)+1,$to,$subject,$body,$cv,$hash,'Draft',$key,$now,$evidence,$profile,$appointment,$preference,$analysis,$language,$prompt,$model,$request
            FROM Outreach WHERE ProfessorId=$prof
            """, ("$id", id), ("$prof", professorId), ("$to", recipient), ("$subject", subject),
            ("$body", body), ("$cv", cvPath), ("$hash", hash), ("$key", idempotencyKey),
            ("$now", DateTimeOffset.UtcNow.ToString("O")), ("$evidence", evidenceJson), ("$profile", profileId),
            ("$appointment", appointmentId), ("$preference", preferenceId), ("$analysis", analysisArtifactId), ("$language", language),
            ("$prompt", promptVersion), ("$model", model), ("$request", draftRequestJson));
        insert.Transaction = transaction;
        insert.ExecuteNonQuery();
        using (var change = LocalDatabase.Command(connection,
            "INSERT INTO DraftChange(Id,DraftId,Revision,ChangeKind,SnapshotJson,CreatedAt) VALUES($id,$draft,1,'Generated',$snapshot,$now)",
            ("$id", Guid.NewGuid().ToString("N")), ("$draft", id),
            ("$snapshot", SnapshotJson(professorId, recipient, subject, body, cvPath, hash, 1, evidenceJson, profileId, appointmentId,
                preferenceId, analysisArtifactId, language, promptVersion, model, draftRequestJson)), ("$now", DateTimeOffset.UtcNow.ToString("O"))))
        {
            change.Transaction = transaction;
            change.ExecuteNonQuery();
        }
        transaction.Commit();
        return Get(id);
    }

    public Draft Get(string id)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection,
            "SELECT Id,ProfessorId,Version,Recipient,Subject,Body,CvPath,CvHash,State,Revision,AppointmentId,PreferenceId,AnalysisArtifactId,Language,PromptVersion,Model,DraftRequestJson,EvidenceJson,ProfileId FROM Outreach WHERE Id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("DRAFT_NOT_FOUND");
        return new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8), reader.GetInt32(9),
            reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12), reader.GetString(13), reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15), reader.GetString(16), reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetString(18));
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
        using var transaction = connection.BeginTransaction();
        string professorId;
        string evidenceJson;
        string? profileId;
        string? appointmentId;
        string? preferenceId;
        string? analysisArtifactId;
        string language;
        string promptVersion;
        string? model;
        string draftRequestJson;
        using (var current = LocalDatabase.Command(connection, """
            SELECT ProfessorId,EvidenceJson,ProfileId,AppointmentId,PreferenceId,AnalysisArtifactId,Language,PromptVersion,Model,DraftRequestJson
            FROM Outreach WHERE Id=$id AND Revision=$revision AND State IN ('Draft','Failed')
            """, ("$id", id), ("$revision", expectedRevision)))
        {
            current.Transaction = transaction;
            using var reader = current.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("DRAFT_CHANGED_OR_LOCKED");
            professorId = reader.GetString(0);
            evidenceJson = reader.GetString(1);
            profileId = reader.IsDBNull(2) ? null : reader.GetString(2);
            appointmentId = reader.IsDBNull(3) ? null : reader.GetString(3);
            preferenceId = reader.IsDBNull(4) ? null : reader.GetString(4);
            analysisArtifactId = reader.IsDBNull(5) ? null : reader.GetString(5);
            language = reader.GetString(6);
            promptVersion = reader.GetString(7);
            model = reader.IsDBNull(8) ? null : reader.GetString(8);
            draftRequestJson = reader.GetString(9);
        }
        using var command = LocalDatabase.Command(connection, """
            UPDATE Outreach SET Recipient=$to,Subject=$subject,Body=$body,CvPath=$cv,CvHash=$hash,
            Revision=Revision+1,State='Draft',Error=NULL,SnapshotJson=NULL
            WHERE Id=$id AND Revision=$revision AND State IN ('Draft','Failed')
            """, ("$to", recipient), ("$subject", subject), ("$body", body), ("$cv", cvPath), ("$hash", hash), ("$id", id), ("$revision", expectedRevision));
        command.Transaction = transaction;
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("DRAFT_CHANGED_OR_LOCKED");
        using (var change = LocalDatabase.Command(connection,
            "INSERT INTO DraftChange(Id,DraftId,Revision,ChangeKind,SnapshotJson,CreatedAt) VALUES($id,$draft,$revision,'Edited',$snapshot,$now)",
            ("$id", Guid.NewGuid().ToString("N")), ("$draft", id), ("$revision", expectedRevision + 1),
            ("$snapshot", SnapshotJson(professorId, recipient, subject, body, cvPath, hash, expectedRevision + 1, evidenceJson, profileId,
                appointmentId, preferenceId, analysisArtifactId, language, promptVersion, model, draftRequestJson)),
            ("$now", DateTimeOffset.UtcNow.ToString("O"))))
        {
            change.Transaction = transaction;
            change.ExecuteNonQuery();
        }
        transaction.Commit();
        return Get(id);
    }

    public IReadOnlyList<DraftChange> ListChanges(string draftId)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection,
            "SELECT Id,DraftId,Revision,ChangeKind,SnapshotJson,CreatedAt FROM DraftChange WHERE DraftId=$draft ORDER BY Revision,Id",
            ("$draft", draftId));
        using var reader = command.ExecuteReader();
        var changes = new List<DraftChange>();
        while (reader.Read()) changes.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        return changes;
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
        string senderAccount;
        using (var reader = lookup.ExecuteReader())
        {
            if (!reader.Read()) throw new InvalidOperationException("SEND_NOT_UNKNOWN");
            if (reader.IsDBNull(1) || gmail.Account is null || !reader.GetString(1).Equals(gmail.Account, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("GMAIL_ACCOUNT_MISMATCH");
            senderAccount = reader.GetString(1);
            snapshot = JsonSerializer.Deserialize<SendSnapshot>(reader.GetString(0))!;
        }
        IReadOnlyList<GmailSentCandidate> candidates;
        try { candidates = await gmail.FindSentCandidatesAsync(snapshot, cancellationToken); }
        catch (Exception error)
        {
            SaveDeliveryCheck(id, senderAccount, snapshot, "Failed", null, null, error.GetType().Name);
            throw;
        }
        if (candidates.Count == 0)
        {
            SaveDeliveryCheck(id, senderAccount, snapshot, "NotFound", null, null, null);
            return false;
        }
        if (candidates.Count > 1)
        {
            SaveDeliveryCheck(id, senderAccount, snapshot, "Ambiguous", null, null, "MULTIPLE_SENT_MATCHES");
            return false;
        }
        var receipt = new SendReceipt(candidates[0].ProviderMessageId, candidates[0].ThreadId);
        using var updateConnection = database.Open();
        using var transaction = updateConnection.BeginTransaction();
        using (var update = LocalDatabase.Command(updateConnection,
            "UPDATE Outreach SET State='Sent',MessageId=$message,ThreadId=$thread,Error=NULL WHERE Id=$id AND State='Unknown'",
            ("$message", receipt.MessageId), ("$thread", receipt.ThreadId), ("$id", id)))
        {
            update.Transaction = transaction;
            if (update.ExecuteNonQuery() != 1) throw new InvalidOperationException("SEND_STATE_CONFLICT");
        }
        using (var attempt = LocalDatabase.Command(updateConnection, """
            UPDATE SendAttempt SET State='Sent',ProviderMessageId=$message,ThreadId=$thread,Error=NULL,FinishedAt=$finished
            WHERE DraftId=$draft AND State='Unknown'
            """, ("$message", receipt.MessageId), ("$thread", receipt.ThreadId), ("$finished", DateTimeOffset.UtcNow.ToString("O")), ("$draft", id)))
        { attempt.Transaction = transaction; attempt.ExecuteNonQuery(); }
        InsertDeliveryCheck(updateConnection, transaction, id, senderAccount, snapshot, "Found", receipt.MessageId, receipt.ThreadId, null);
        transaction.Commit();
        return true;
    }

    private static string SnapshotJson(string professorId, string recipient, string subject, string body, string? cvPath, string? cvHash,
        int revision, string? evidenceJson, string? profileId, string? appointmentId, string? preferenceId, string? analysisArtifactId,
        string language, string promptVersion, string? model, string draftRequestJson)
        => JsonSerializer.Serialize(new
        {
            professorId, recipient, subject, body, cvPath, cvHash, revision, evidenceJson, profileId, appointmentId, preferenceId,
            analysisArtifactId, language, promptVersion, model, draftRequestJson
        });

    public async Task<int> SyncRepliesAsync(GmailService gmail, CancellationToken cancellationToken = default)
    {
        if (gmail.Account is null) throw new InvalidOperationException("GMAIL_NOT_CONNECTED");
        using var connection = database.Open();
        using var query = LocalDatabase.Command(connection,
            "SELECT Id,MessageId,ThreadId FROM Outreach WHERE State='Sent' AND SenderAccount=$account AND MessageId IS NOT NULL AND ThreadId IS NOT NULL", ("$account", gmail.Account));
        var sent = new List<(string Id, string Message, string Thread)>();
        using (var reader = query.ExecuteReader()) while (reader.Read()) sent.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        var replies = 0;
        foreach (var item in sent)
        {
            var thread = await gmail.GetThreadStatusAsync(item.Thread, item.Message, cancellationToken);
            var now = DateTimeOffset.UtcNow.ToString("O");
            var owner = ExistingThreadOwner(connection, gmail.Account, item.Thread);
            var ambiguous = owner is not null && owner != item.Id;
            foreach (var message in thread.Messages ?? [])
            {
                var relation = ambiguous && !message.IsSelf ? "Ambiguous" : message.RelationState;
                using var saveMessage = LocalDatabase.Command(connection, """
                    INSERT INTO EmailMessage(Id,Account,ThreadId,ProviderMessageId,OutreachId,FromAddress,ToAddress,InReplyTo,ReferencesJson,IsSelf,RelationState,InternalDate,RecordedAt)
                    VALUES($id,$account,$thread,$message,$outreach,$from,$to,$inReply,$references,$self,$relation,$date,$recorded)
                    ON CONFLICT(Account,ProviderMessageId) DO UPDATE SET ThreadId=excluded.ThreadId,OutreachId=excluded.OutreachId,
                    FromAddress=excluded.FromAddress,ToAddress=excluded.ToAddress,InReplyTo=excluded.InReplyTo,ReferencesJson=excluded.ReferencesJson,
                    IsSelf=excluded.IsSelf,RelationState=excluded.RelationState,InternalDate=excluded.InternalDate,RecordedAt=excluded.RecordedAt
                    """, ("$id", Guid.NewGuid().ToString("N")), ("$account", gmail.Account), ("$thread", item.Thread),
                    ("$message", message.Id), ("$outreach", item.Id), ("$from", string.IsNullOrWhiteSpace(message.FromAddress) ? "unknown" : message.FromAddress),
                    ("$to", message.ToAddress), ("$inReply", message.InReplyTo), ("$references", message.ReferencesJson), ("$self", message.IsSelf ? 1 : 0),
                    ("$relation", relation), ("$date", message.InternalDate?.ToString("O")), ("$recorded", now));
                saveMessage.ExecuteNonQuery();
            }
            var replyState = ambiguous || (thread.Messages ?? []).Any(message => message.RelationState == "Ambiguous") ? "Ambiguous" : thread.HasReply ? "Replied" : "NoReply";
            using var save = LocalDatabase.Command(connection, """
                INSERT INTO EmailThread(Account,ThreadId,OutreachId,ReplyState,SyncedAt) VALUES($account,$thread,$id,$state,$now)
                ON CONFLICT(Account,ThreadId) DO UPDATE SET ReplyState=excluded.ReplyState,SyncedAt=excluded.SyncedAt
                """, ("$account", gmail.Account), ("$thread", item.Thread), ("$id", ambiguous && owner is not null ? owner : item.Id),
                ("$state", replyState), ("$now", now));
            save.ExecuteNonQuery(); if (replyState == "Replied") replies++;
        }
        return replies;
    }

    public IReadOnlyList<DeliveryCheckRecord> DeliveryChecks(string draftId)
    {
        using var connection = database.Open();
        using var query = LocalDatabase.Command(connection, "SELECT Id,DraftId,AttemptId,SenderAccount,RfcMessageId,Outcome,ProviderMessageId,ThreadId,Error,CheckedAt FROM DeliveryCheck WHERE DraftId=$draft ORDER BY CheckedAt DESC,Id DESC", ("$draft", draftId));
        using var reader = query.ExecuteReader();
        var checks = new List<DeliveryCheckRecord>();
        while (reader.Read()) checks.Add(new(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetString(8), DateTimeOffset.Parse(reader.GetString(9))));
        return checks;
    }

    public IReadOnlyList<ReplyMessageRecord> ReplyMessages(string? outreachId = null)
    {
        using var connection = database.Open();
        using var query = LocalDatabase.Command(connection, "SELECT Id,Account,ThreadId,ProviderMessageId,OutreachId,FromAddress,ToAddress,InReplyTo,ReferencesJson,IsSelf,RelationState,InternalDate,RecordedAt FROM EmailMessage WHERE $outreach IS NULL OR OutreachId=$outreach ORDER BY RecordedAt DESC,Id DESC", ("$outreach", outreachId));
        using var reader = query.ExecuteReader();
        var messages = new List<ReplyMessageRecord>();
        while (reader.Read()) messages.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8), reader.GetBoolean(9), reader.GetString(10), reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11)), DateTimeOffset.Parse(reader.GetString(12))));
        return messages;
    }
    public int ReplyCount()
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "SELECT COUNT(*) FROM EmailThread WHERE ReplyState='Replied'");
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private void SaveDeliveryCheck(string draftId, string senderAccount, SendSnapshot snapshot, string outcome, string? providerMessageId, string? threadId, string? error)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        InsertDeliveryCheck(connection, transaction, draftId, senderAccount, snapshot, outcome, providerMessageId, threadId, error);
        transaction.Commit();
    }

    private static void InsertDeliveryCheck(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction,
        string draftId, string senderAccount, SendSnapshot snapshot, string outcome, string? providerMessageId, string? threadId, string? error)
    {
        using var attempt = LocalDatabase.Command(connection, "SELECT Id FROM SendAttempt WHERE DraftId=$draft AND RfcMessageId=$rfc ORDER BY CreatedAt DESC LIMIT 1", ("$draft", draftId), ("$rfc", snapshot.RfcMessageId));
        attempt.Transaction = transaction;
        var attemptId = attempt.ExecuteScalar() as string;
        using var insert = LocalDatabase.Command(connection, "INSERT INTO DeliveryCheck(Id,DraftId,AttemptId,SenderAccount,RfcMessageId,Outcome,ProviderMessageId,ThreadId,Error,CheckedAt) VALUES($id,$draft,$attempt,$account,$rfc,$outcome,$message,$thread,$error,$checked)",
            ("$id", Guid.NewGuid().ToString("N")), ("$draft", draftId), ("$attempt", attemptId), ("$account", senderAccount), ("$rfc", snapshot.RfcMessageId),
            ("$outcome", outcome), ("$message", providerMessageId), ("$thread", threadId), ("$error", error), ("$checked", DateTimeOffset.UtcNow.ToString("O")));
        insert.Transaction = transaction;
        insert.ExecuteNonQuery();
    }

    private static string? ExistingThreadOwner(Microsoft.Data.Sqlite.SqliteConnection connection, string account, string threadId)
    {
        using var command = LocalDatabase.Command(connection, "SELECT OutreachId FROM EmailThread WHERE Account=$account AND ThreadId=$thread", ("$account", account), ("$thread", threadId));
        return command.ExecuteScalar() as string;
    }
}
