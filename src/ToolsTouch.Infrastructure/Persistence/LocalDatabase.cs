using Microsoft.Data.Sqlite;

namespace ToolsTouch.Core;

public sealed class LocalDatabase(string path)
{
    public bool Pooling { get; init; } = true;
    public string DatabasePath => Path.GetFullPath(path);
    public string ArtifactDirectory => Path.Combine(Path.GetDirectoryName(DatabasePath)!, "artifacts");

    public SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, ForeignKeys = true, DefaultTimeout = 15, Pooling = Pooling
        }.ToString());
        connection.Open();
        return connection;
    }

    public void Initialize()
    {
        new MigrationRunner(this).Run();
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
