using Microsoft.Data.Sqlite;
using ToolsTouch.Core;

var directory = Path.Combine(Path.GetTempPath(), "tools-touch-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var database = new LocalDatabase(Path.Combine(directory, "test.db"));
    database.Initialize();
    database.Initialize(); // Migration is repeatable.
    using (var connection = database.Open())
    using (var command = LocalDatabase.Command(connection,
        "INSERT INTO Professor VALUES('prof','Test','University','https://example.org/prof',NULL,'[]','now')"))
        command.ExecuteNonQuery();
    var service = new OutreachService(database);
    Draft Create(string key) => service.CreateDraft("prof", "test@example.org", "Research", "Hello", null, key);
    var first = Create("one");
    Check(Create("one").Id == first.Id, "draft retry must be idempotent");
    var second = Create("two");
    Check(second.Version == first.Version + 1 && service.Get(first.Id).Body == "Hello", "regeneration preserves old version");
    await Throws<InvalidOperationException>(() => Task.FromResult(service.CreateDraft("prof", "test@example.org", "Changed", "Hello", null, "one")));

    var gate = new TaskCompletionSource<SendReceipt>();
    var transport = new FakeTransport(() => gate.Task);
    var sending = service.SendAsync(first.Id, transport);
    await Throws<InvalidOperationException>(() => service.SendAsync(first.Id, transport));
    Check(transport.Calls == 1, "double click must not send twice");
    gate.SetResult(new("message", "thread"));
    await sending;
    Check(service.Get(first.Id).State == "Sent", "successful send persisted");
    await Throws<IOException>(() => service.SendAsync(second.Id, new FakeTransport(() => throw new IOException("connection lost"))));
    Check(service.Get(second.Id).State == "Unknown", "transport failure must remain ambiguous");
    await Throws<InvalidOperationException>(() => service.SendAsync(second.Id, transport));

    var third = Create("three");
    await Throws<SendRejectedException>(() => service.SendAsync(third.Id, new FakeTransport(() => throw new SendRejectedException("rejected"))));
    Check(service.Get(third.Id).State == "Failed", "explicit rejection can be retried");
    var cv = Path.Combine(directory, "cv.pdf");
    await File.WriteAllTextAsync(cv, "original");
    var attached = service.CreateDraft("prof", "test@example.org", "Research", "Hello", cv, "cv");
    await File.WriteAllTextAsync(cv, "changed");
    await Throws<InvalidOperationException>(() => service.SendAsync(attached.Id, transport));
    Check(service.Get(attached.Id).State == "Draft", "changed CV requires review before sending");

    using (var connection = database.Open())
    using (var command = LocalDatabase.Command(connection,"UPDATE Outreach SET State='Sending' WHERE Id=$id", ("$id", third.Id)))
        command.ExecuteNonQuery();
    new OutreachService(new LocalDatabase(Path.Combine(directory, "test.db"))).RecoverInterruptedSends();
    Check(service.Get(third.Id).State == "Unknown", "restart must not retry interrupted sends");
    Console.WriteLine("PASS: migration, draft idempotency/conflict/versioning, duplicate send, ambiguous failure, rejection, CV mutation, restart recovery");
    await ResearchTests.RunAsync(database, directory, args);
    await GmailTests.RunAsync(database);
    await AccountTests.RunAsync(directory);
    await SearchTests.RunAsync();
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(directory, true);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception("Expected " + typeof(T).Name);
}
sealed class FakeTransport(Func<Task<SendReceipt>> send) : IMailTransport
{
    public int Calls { get; private set; }
    public Task<SendReceipt> SendAsync(SendSnapshot snapshot, CancellationToken cancellationToken)
    {
        Calls++;
        return send();
    }
}
