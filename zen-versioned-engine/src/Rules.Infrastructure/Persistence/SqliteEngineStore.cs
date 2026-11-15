using Microsoft.Data.Sqlite;
using Rules.Application;
using Rules.Domain;

namespace Rules.Infrastructure.Persistence;

// Append-only snapshots are intentionally simple for this POC. They preserve every commit,
// with a database transaction and revision check across processes. See production limitations.
public sealed class SqliteEngineStore : IEngineStore
{
    private readonly string connectionString;
    public SqliteEngineStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 15, Pooling = true }.ToString();
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS Snapshots (Revision INTEGER PRIMARY KEY, Json TEXT NOT NULL, Hash TEXT NOT NULL, CreatedAt TEXT NOT NULL);
            CREATE TRIGGER IF NOT EXISTS immutable_snapshot_update BEFORE UPDATE ON Snapshots BEGIN SELECT RAISE(ABORT,'History is immutable'); END;
            CREATE TRIGGER IF NOT EXISTS immutable_snapshot_delete BEFORE DELETE ON Snapshots BEGIN SELECT RAISE(ABORT,'History is immutable'); END;
            INSERT OR IGNORE INTO Snapshots VALUES (0, $json, $hash, $at);
            """;
        var initial = new EngineState(); command.Parameters.AddWithValue("$json", Json.Write(initial)); command.Parameters.AddWithValue("$hash", Json.Hash(initial)); command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    public StateSnapshot Read()
    {
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT Revision, Json, Hash FROM Snapshots ORDER BY Revision DESC LIMIT 1";
        using var r = command.ExecuteReader(); r.Read();
        var state = Json.Read<EngineState>(r.GetString(1));
        if (Json.Hash(state) != r.GetString(2)) throw new InvalidDataException("Snapshot checksum mismatch");
        return new(r.GetInt64(0), state);
    }
    public bool TryCommit(long expectedRevision, EngineState state)
    {
        using var db = Open(); using var transaction = db.BeginTransaction(deferred: false);
        using var command = db.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT MAX(Revision) FROM Snapshots";
        if (Convert.ToInt64(command.ExecuteScalar()) != expectedRevision) return false;
        command.CommandText = "INSERT INTO Snapshots VALUES ($revision,$json,$hash,$at)";
        command.Parameters.AddWithValue("$revision", expectedRevision + 1); command.Parameters.AddWithValue("$json", Json.Write(state));
        command.Parameters.AddWithValue("$hash", Json.Hash(state)); command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery(); transaction.Commit(); return true;
    }
}
