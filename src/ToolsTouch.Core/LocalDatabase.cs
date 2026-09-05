using Microsoft.Data.Sqlite;

namespace ToolsTouch.Core;

public sealed class LocalDatabase(string path)
{
    public SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, ForeignKeys = true, DefaultTimeout = 15
        }.ToString());
        connection.Open();
        return connection;
    }

    public void Initialize()
    {
        using var connection = Open();
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE IF NOT EXISTS SchemaVersion (Version INTEGER PRIMARY KEY);";
            setup.ExecuteNonQuery();
        }
        using var transaction = connection.BeginTransaction();
        var assembly = typeof(LocalDatabase).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.Contains(".Migrations.") && name.EndsWith(".sql")).Order())
        {
            var version = int.Parse(resource.Split(".Migrations.")[1].Split('_')[0]);
            using var check = connection.CreateCommand();
            check.Transaction = transaction;
            check.CommandText = "SELECT COUNT(*) FROM SchemaVersion WHERE Version=$version";
            check.Parameters.AddWithValue("$version", version);
            if (Convert.ToInt32(check.ExecuteScalar()) != 0) continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            using var migration = connection.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = reader.ReadToEnd() + "\nINSERT INTO SchemaVersion VALUES($version);";
            migration.Parameters.AddWithValue("$version", version);
            migration.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public static SqliteCommand Command(SqliteConnection connection, string sql,
        params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}
