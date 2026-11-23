using Microsoft.Data.Sqlite;

namespace Animanager.Core.Storage;

internal sealed record StoredDuration(string Hash, long Size, long Modified, double? Seconds)
{
    internal static StoredDuration? Inspect(string hash, string path, Func<double?> read)
    {
        try
        {
            var file = new FileInfo(path);
            long size = file.Length, modified = file.LastWriteTimeUtc.Ticks;
            double? seconds = read();
            file.Refresh();
            return file.Exists && file.Length == size && file.LastWriteTimeUtc.Ticks == modified
                ? new(hash, size, modified, seconds) : null;
        }
        // Failure to extract optional metadata must not fail an otherwise valid import.
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException)) { return null; }
    }
}

public sealed partial class AssetDatabase
{
    // Derived data. Rebuilding an index or opening an older library can leave this table absent.
    private const string DurationTable = "browse_duration_v1";

    internal Dictionary<string, StoredDuration> ReadDurations()
    {
        lock (_gate)
        {
            using var db = Open(_path, SqliteOpenMode.ReadOnly);
            using var exists = Command(db, $"SELECT 1 FROM sqlite_master WHERE type='table' AND name='{DurationTable}'");
            var result = new Dictionary<string, StoredDuration>(StringComparer.Ordinal);
            if (exists.ExecuteScalar() == null) return result;
            using var query = Command(db, $"SELECT hash,size,modified,seconds FROM {DurationTable}");
            using var reader = query.ExecuteReader();
            while (reader.Read())
            {
                string hash = reader.GetString(0);
                result[hash] = new(hash, reader.GetInt64(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetDouble(3));
            }
            return result;
        }
    }

    internal void SaveDurations(List<StoredDuration> entries)
    {
        if (entries.Count == 0) return;
        lock (_gate)
        {
            using var db = Open(_path);
            using var tx = db.BeginTransaction();
            WriteDurations(db, tx, entries);
            tx.Commit();
        }
    }

    private static void WriteDurations(SqliteConnection db, SqliteTransaction tx, IEnumerable<StoredDuration> entries)
    {
        using var create = Command(db, $"CREATE TABLE IF NOT EXISTS {DurationTable} (hash TEXT PRIMARY KEY,size INTEGER NOT NULL,modified INTEGER NOT NULL,seconds REAL)", tx);
        create.ExecuteNonQuery();
        using var cleanup = Command(db, $"CREATE TRIGGER IF NOT EXISTS duration_delete AFTER DELETE ON assets BEGIN DELETE FROM {DurationTable} WHERE hash=old.hash; END", tx);
        cleanup.ExecuteNonQuery();
        using var cmd = Command(db, $"""
            INSERT OR REPLACE INTO {DurationTable} (hash,size,modified,seconds)
            SELECT $hash,$size,$modified,$seconds WHERE EXISTS (SELECT 1 FROM assets WHERE hash=$hash)
            """, tx);
        var hash = cmd.Parameters.Add("$hash", SqliteType.Text);
        var size = cmd.Parameters.Add("$size", SqliteType.Integer);
        var modified = cmd.Parameters.Add("$modified", SqliteType.Integer);
        var seconds = cmd.Parameters.Add("$seconds", SqliteType.Real);
        foreach (var entry in entries)
        {
            hash.Value = entry.Hash;
            size.Value = entry.Size;
            modified.Value = entry.Modified;
            seconds.Value = (object?)entry.Seconds ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
    }
}
