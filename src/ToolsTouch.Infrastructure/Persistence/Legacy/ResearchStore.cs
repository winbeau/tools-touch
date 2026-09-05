using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ToolsTouch.Core;

public sealed class ResearchStore(LocalDatabase database)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static string Now => DateTimeOffset.UtcNow.ToString("O");

    public Professor SaveProfessor(string name, string institution, string homepage, string? email, Evidence[] evidence, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(institution);
        if (!Uri.TryCreate(homepage, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("INVALID_HOMEPAGE");
        homepage = new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri.TrimEnd('/');
        if (evidence.Length == 0 || evidence.Any(item => string.IsNullOrWhiteSpace(item.Claim) ||
            !Uri.TryCreate(item.Url, UriKind.Absolute, out var source) || source.Scheme is not ("http" or "https")))
            throw new InvalidOperationException("EVIDENCE_REQUIRED");
        var input = Serialize(new { name, institution, homepage, email, evidence });
        return WriteOnce("save_professor", key, input, (connection, transaction) =>
        {
            var id = Guid.NewGuid().ToString("N");
            using var command = LocalDatabase.Command(connection, """
                INSERT INTO Professor(Id,Name,Institution,Homepage,Email,EvidenceJson,UpdatedAt)
                VALUES($id,$name,$institution,$homepage,$email,$evidence,$now)
                ON CONFLICT(Homepage) DO UPDATE SET Name=excluded.Name,Institution=excluded.Institution,
                  Email=COALESCE(excluded.Email,Professor.Email),EvidenceJson=excluded.EvidenceJson,UpdatedAt=excluded.UpdatedAt
                RETURNING Id,Name,Institution,Homepage,Email,EvidenceJson
                """, ("$id", id), ("$name", name.Trim()), ("$institution", institution.Trim()),
                ("$homepage", homepage), ("$email", email), ("$evidence", Serialize(evidence)), ("$now", Now));
            command.Transaction = transaction;
            using var reader = command.ExecuteReader();
            reader.Read();
            return ReadProfessor(reader);
        });
    }

    public T WriteOnce<T>(string tool, string key, string input, Func<SqliteConnection, SqliteTransaction, T> write)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using (var lookup = LocalDatabase.Command(connection, "SELECT Tool,InputHash,ResultJson FROM ToolWrite WHERE IdempotencyKey=$key", ("$key", key)))
        {
            lookup.Transaction = transaction;
            using var reader = lookup.ExecuteReader();
            if (reader.Read())
            {
                if (reader.GetString(0) != tool || reader.GetString(1) != hash) throw new InvalidOperationException("IDEMPOTENCY_CONFLICT");
                return JsonSerializer.Deserialize<T>(reader.GetString(2), Json)!;
            }
        }
        var result = write(connection, transaction);
        using var save = LocalDatabase.Command(connection, "INSERT INTO ToolWrite VALUES($key,$tool,$hash,$result)",
            ("$key", key), ("$tool", tool), ("$hash", hash), ("$result", Serialize(result)));
        save.Transaction = transaction;
        save.ExecuteNonQuery();
        transaction.Commit();
        return result;
    }

    public IReadOnlyList<Professor> SearchProfessors(string query = "", int limit = 100)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            SELECT Id,Name,Institution,Homepage,Email,EvidenceJson FROM Professor
            WHERE instr(lower(Name || ' ' || Institution || ' ' || EvidenceJson),lower($query))>0 OR $query=''
            ORDER BY UpdatedAt DESC LIMIT $limit
            """, ("$query", query), ("$limit", Math.Clamp(limit, 1, 100)));
        using var reader = command.ExecuteReader();
        var result = new List<Professor>();
        while (reader.Read()) result.Add(ReadProfessor(reader));
        return result;
    }

    public Professor GetProfessor(string id)
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "SELECT Id,Name,Institution,Homepage,Email,EvidenceJson FROM Professor WHERE Id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("PROFESSOR_NOT_FOUND");
        return ReadProfessor(reader);
    }

    private static Professor ReadProfessor(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), JsonSerializer.Deserialize<Evidence[]>(reader.GetString(5), Json)!);

    public AgentRun CreateRun(string kind, string inputJson, string budgetJson)
    {
        var id = Guid.NewGuid().ToString("N");
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO AgentRun(Id,Kind,InputJson,State,Stage,BudgetJson,CreatedAt,UpdatedAt)
            VALUES($id,$kind,$input,'Queued','Prepare',$budget,$now,$now)
            """, ("$id", id), ("$kind", kind), ("$input", inputJson), ("$budget", budgetJson), ("$now", Now));
        command.ExecuteNonQuery();
        return GetRun(id);
    }

    public AgentRun GetRun(string id) => ListRuns().FirstOrDefault(run => run.Id == id) ?? throw new KeyNotFoundException("RUN_NOT_FOUND");

    public IReadOnlyList<AgentRun> ListRuns()
    {
        using var connection = database.Open();
        using var command = LocalDatabase.Command(connection, "SELECT Id,Kind,InputJson,State,Stage,CheckpointJson,BudgetJson,Error FROM AgentRun ORDER BY CreatedAt DESC");
        using var reader = command.ExecuteReader();
        var result = new List<AgentRun>();
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        return result;
    }

    public void UpdateRun(string id, string state, string stage, string? checkpointJson = null, string? error = null,
        Action<SqliteConnection, SqliteTransaction>? saveStageResult = null)
    {
        if (state is not ("Queued" or "Running" or "Completed" or "Partial" or "Failed" or "Cancelled")) throw new ArgumentException("INVALID_RUN_STATE");
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var update = LocalDatabase.Command(connection, """
            UPDATE AgentRun SET State=$state,Stage=$stage,CheckpointJson=COALESCE($checkpoint,CheckpointJson),Error=$error,UpdatedAt=$now WHERE Id=$id
            """, ("$state", state), ("$stage", stage), ("$checkpoint", checkpointJson), ("$error", error), ("$now", Now), ("$id", id));
        update.Transaction = transaction;
        if (update.ExecuteNonQuery() != 1) throw new KeyNotFoundException("RUN_NOT_FOUND");
        saveStageResult?.Invoke(connection, transaction);
        AppendEvent(connection, transaction, id, "state_changed", Serialize(new { state, stage, error }));
        transaction.Commit();
    }

    public void AppendEvent(string runId, string type, string payloadJson)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        AppendEvent(connection, transaction, runId, type, payloadJson);
        transaction.Commit();
    }

    private static void AppendEvent(SqliteConnection connection, SqliteTransaction transaction, string runId, string type, string payloadJson)
    {
        using var command = LocalDatabase.Command(connection, """
            INSERT INTO AgentRunEvent(RunId,Sequence,Type,PayloadJson,CreatedAt)
            SELECT $run,COALESCE(MAX(Sequence),0)+1,$type,$payload,$now FROM AgentRunEvent WHERE RunId=$run
            """, ("$run", runId), ("$type", type), ("$payload", payloadJson), ("$now", Now));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    public void RecoverInterruptedRuns()
    {
        foreach (var run in ListRuns().Where(run => run.State == "Running"))
            UpdateRun(run.Id, "Partial", run.Stage, error: "PROCESS_INTERRUPTED");
    }
}
