using ToolsTouch.Application;
using ToolsTouch.Core;

static class SendConfirmationTests
{
    public static async Task RunAsync(LocalDatabase database, string directory)
    {
        var outreach = new OutreachService(database);
        var artifacts = new ArtifactStore(database.ArtifactDirectory);
        var current = DateTimeOffset.UtcNow;
        var coordinator = new SendCoordinator(database, outreach, artifacts, () => current);
        var cvPath = Path.Combine(directory, "send-confirmation-cv.pdf");
        var originalCv = "original-confirmed-cv"u8.ToArray();
        await File.WriteAllBytesAsync(cvPath, originalCv);
        var draft = outreach.CreateDraft("prof", "recipient@example.org", "Confirm subject", "Confirm body", cvPath, "send-confirmation-stale");

        var preview = coordinator.Prepare(draft.Id, "sender@example.org");
        Check(preview.DraftRevision == draft.Revision && preview.AttachmentBytes == originalCv.Length && preview.SnapshotHash.Length == 64,
            "prepare freezes current draft revision, attachment size and snapshot hash");
        await File.WriteAllTextAsync(cvPath, "changed-before-confirm");
        await ThrowsCode(() => Task.FromResult(coordinator.Confirm(preview.ConfirmationId, "sender@example.org")), "SEND_SNAPSHOT_STALE");
        await File.WriteAllBytesAsync(cvPath, originalCv);

        var editedPreview = coordinator.Prepare(draft.Id, "sender@example.org");
        outreach.Edit(draft.Id, draft.Revision, draft.Recipient, draft.Subject, "Edited before confirmation", draft.CvPath);
        await ThrowsCode(() => Task.FromResult(coordinator.Confirm(editedPreview.ConfirmationId, "sender@example.org")), "SEND_SNAPSHOT_STALE");

        var sendDraft = outreach.CreateDraft("prof", "recipient@example.org", "Immutable subject", "Immutable body", cvPath, "send-confirmation-send");
        var sendPreview = coordinator.Prepare(sendDraft.Id, "sender@example.org");
        var artifact = ReadArtifact(database, sendPreview.MimeArtifactId);
        var originalMime = artifacts.ReadBytes(artifact);
        var attempt = coordinator.Confirm(sendPreview.ConfirmationId, "sender@example.org");
        await ThrowsCode(() => Task.FromResult(coordinator.Confirm(sendPreview.ConfirmationId, "sender@example.org")), "SEND_CONFIRMATION_CONSUMED");
        await File.WriteAllTextAsync(cvPath, "changed-after-confirm");
        var transport = new RecordingTransport(() => Task.FromResult(new SendTransportReceipt("provider-message", "provider-thread")));
        var sent = await coordinator.SendAsync(attempt.Id, transport);
        Check(sent.State == "Sent" && outreach.Get(sendDraft.Id).State == "Sent" && transport.Calls == 1 &&
            transport.Envelope!.MimeBytes.SequenceEqual(originalMime),
            "confirmed transmission uses the immutable MIME artifact after attachment path mutation");
        await ThrowsCode(() => coordinator.SendAsync(attempt.Id, transport), "SEND_ATTEMPT_NOT_SENDABLE");

        var doubleDraft = outreach.CreateDraft("prof", "recipient@example.org", "Double subject", "Double body", null, "send-confirmation-double");
        var doubleAttempt = coordinator.Confirm(coordinator.Prepare(doubleDraft.Id, "sender@example.org").ConfirmationId, "sender@example.org");
        var gate = new TaskCompletionSource<SendTransportReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gated = new RecordingTransport(() => gate.Task);
        var firstSend = coordinator.SendAsync(doubleAttempt.Id, gated);
        await gated.Started.Task;
        await ThrowsCode(() => coordinator.SendAsync(doubleAttempt.Id, gated), "SEND_ATTEMPT_NOT_SENDABLE");
        gate.SetResult(new("double-message", "double-thread"));
        await firstSend;
        Check(gated.Calls == 1, "concurrent confirmation/send clicks obtain one transport claim");

        var failedDraft = outreach.CreateDraft("prof", "recipient@example.org", "Unknown subject", "Unknown body", null, "send-confirmation-unknown");
        var failedAttempt = coordinator.Confirm(coordinator.Prepare(failedDraft.Id, "sender@example.org").ConfirmationId, "sender@example.org");
        await Throws<IOException>(() => coordinator.SendAsync(failedAttempt.Id, new RecordingTransport(() => throw new IOException("connection lost"))));
        Check(coordinator.GetAttempt(failedAttempt.Id).State == "Unknown" && outreach.Get(failedDraft.Id).State == "Unknown",
            "ambiguous transport failure locks both attempt and draft as Unknown");
        await ThrowsCode(() => Task.FromResult(coordinator.Prepare(failedDraft.Id, "sender@example.org")), "DRAFT_NOT_SENDABLE");

        var recoveryDraft = outreach.CreateDraft("prof", "recipient@example.org", "Recovery subject", "Recovery body", null, "send-confirmation-recovery");
        var recoveryAttempt = coordinator.Confirm(coordinator.Prepare(recoveryDraft.Id, "sender@example.org").ConfirmationId, "sender@example.org");
        coordinator.RecoverInterruptedSends();
        Check(coordinator.GetAttempt(recoveryAttempt.Id).State == "Unknown" && outreach.Get(recoveryDraft.Id).State == "Unknown",
            "restart recovery moves unclaimed Sending attempts to Unknown");

        var expiringDraft = outreach.CreateDraft("prof", "recipient@example.org", "Expiry subject", "Expiry body", null, "send-confirmation-expiry");
        var expiring = coordinator.Prepare(expiringDraft.Id, "sender@example.org");
        current = expiring.ExpiresAt.AddSeconds(1);
        await ThrowsCode(() => Task.FromResult(coordinator.Confirm(expiring.ConfirmationId, "sender@example.org")), "SEND_CONFIRMATION_EXPIRED");

        Console.WriteLine("PASS: immutable MIME preparation, stale confirmation, one-time consumption, transport claim, TOCTOU attachment protection, Unknown recovery and expiry");
    }

    private static Artifact ReadArtifact(LocalDatabase database, string id)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "SELECT Id,Kind,RelativePath,ContentHash,ByteLength,MimeType,CreatedAt FROM Artifact WHERE Id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new Exception("artifact missing");
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(4), reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6)));
    }

    private static async Task ThrowsCode(Func<Task> action, string code)
    {
        try { await action(); }
        catch (InvalidOperationException error) when (error.Message == code) { return; }
        throw new Exception("Expected " + code);
    }

    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class RecordingTransport(Func<Task<SendTransportReceipt>> send) : ISendTransport
    {
        public string? Account => "sender@example.org";
        public int Calls { get; private set; }
        public SendEnvelope? Envelope { get; private set; }
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<SendTransportReceipt> SendAsync(SendEnvelope envelope, CancellationToken cancellationToken)
        {
            Calls++;
            Envelope = envelope;
            Started.TrySetResult(true);
            return send();
        }
    }
}
