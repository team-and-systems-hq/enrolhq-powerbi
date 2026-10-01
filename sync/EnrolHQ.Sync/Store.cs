using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace EnrolHQ.Sync;

/// <summary>Where a table's download has got to.</summary>
/// <param name="Cycle">Counts downloads of this table. Rows are stamped with the cycle that last saw them.</param>
/// <param name="NextPage">The next page to fetch in the current cycle.</param>
/// <param name="Total">How many records the API reported for the current cycle.</param>
/// <param name="UpdatedAfter">The updated_after value the current cycle is using, if any.</param>
/// <param name="StartedAt">When the current cycle started.</param>
/// <param name="CompletedAt">When the current cycle finished, or null while it is unfinished.</param>
/// <param name="Watermark">Start time of the last finished cycle; the next one only needs changes since then.</param>
internal sealed record SyncState(
    int Cycle,
    int NextPage,
    int? Total,
    string? UpdatedAfter,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? Watermark);

/// <summary>
/// The local copy: every record as the JSON the API returned (after masking,
/// when anonymisation is on), plus how far each download has got.
/// </summary>
internal sealed class Store : IDisposable
{
    private readonly SqliteConnection _connection;

    public Store(string path)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
        }

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _connection.Open();
        Execute("PRAGMA journal_mode = WAL");
        Execute(
            """
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS records (
                table_name TEXT NOT NULL,
                id         TEXT NOT NULL,
                json       TEXT NOT NULL,
                cycle      INTEGER NOT NULL,
                fetched_at TEXT NOT NULL,
                PRIMARY KEY (table_name, id)
            ) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS sync_state (
                table_name    TEXT PRIMARY KEY,
                cycle         INTEGER NOT NULL,
                next_page     INTEGER NOT NULL,
                total         INTEGER,
                updated_after TEXT,
                started_at    TEXT,
                completed_at  TEXT,
                watermark     TEXT
            );
            """);
    }

    public string? GetMeta(string key)
    {
        using var command = Command("SELECT value FROM meta WHERE key = $key", ("$key", key));
        return command.ExecuteScalar() as string;
    }

    public void SetMeta(string key, string value) =>
        Execute(
            "INSERT INTO meta (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            ("$key", key),
            ("$value", value));

    public SyncState GetState(string table)
    {
        using var command = Command(
            "SELECT cycle, next_page, total, updated_after, started_at, completed_at, watermark FROM sync_state WHERE table_name = $table",
            ("$table", table));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new SyncState(0, 1, null, null, null, null, null);
        }

        return new SyncState(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            ReadTime(reader, 4),
            ReadTime(reader, 5),
            ReadTime(reader, 6));
    }

    /// <summary>Starts a new download of a table from page 1.</summary>
    public SyncState StartCycle(string table, string? updatedAfter, DateTimeOffset now)
    {
        var previous = GetState(table);
        Execute(
            """
            INSERT INTO sync_state (table_name, cycle, next_page, total, updated_after, started_at, completed_at, watermark)
            VALUES ($table, $cycle, 1, NULL, $updatedAfter, $startedAt, NULL, $watermark)
            ON CONFLICT (table_name) DO UPDATE SET
                cycle = excluded.cycle, next_page = 1, total = NULL, updated_after = excluded.updated_after,
                started_at = excluded.started_at, completed_at = NULL
            """,
            ("$table", table),
            ("$cycle", previous.Cycle + 1),
            ("$updatedAfter", updatedAfter),
            ("$startedAt", Format(now)),
            ("$watermark", previous.Watermark is { } watermark ? Format(watermark) : null));
        return GetState(table);
    }

    /// <summary>
    /// Saves one page and moves the bookmark past it in a single transaction,
    /// so an interrupted run never loses or repeats a page.
    /// </summary>
    public void SavePage(string table, int cycle, int page, int? total, IEnumerable<(string Id, JsonObject Record)> records, DateTimeOffset now)
    {
        using var transaction = _connection.BeginTransaction();
        using (var upsert = _connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText =
                """
                INSERT INTO records (table_name, id, json, cycle, fetched_at) VALUES ($table, $id, $json, $cycle, $fetchedAt)
                ON CONFLICT (table_name, id) DO UPDATE SET json = excluded.json, cycle = excluded.cycle, fetched_at = excluded.fetched_at
                """;
            upsert.Parameters.AddWithValue("$table", table);
            upsert.Parameters.AddWithValue("$cycle", cycle);
            upsert.Parameters.AddWithValue("$fetchedAt", Format(now));
            var id = upsert.Parameters.Add("$id", SqliteType.Text);
            var json = upsert.Parameters.Add("$json", SqliteType.Text);
            foreach (var (recordId, record) in records)
            {
                id.Value = recordId;
                json.Value = record.ToJsonString();
                upsert.ExecuteNonQuery();
            }
        }

        using (var bookmark = _connection.CreateCommand())
        {
            bookmark.Transaction = transaction;
            bookmark.CommandText = "UPDATE sync_state SET next_page = $next, total = $total WHERE table_name = $table";
            bookmark.Parameters.AddWithValue("$next", page + 1);
            bookmark.Parameters.AddWithValue("$total", (object?)total ?? DBNull.Value);
            bookmark.Parameters.AddWithValue("$table", table);
            bookmark.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>
    /// Marks the download finished. After a full download, records this cycle
    /// did not see no longer exist in EnrolHQ and are removed.
    /// </summary>
    /// <returns>How many records were removed.</returns>
    public int CompleteCycle(string table, bool removeUnseen, DateTimeOffset now)
    {
        var state = GetState(table);
        using var transaction = _connection.BeginTransaction();
        var removed = 0;
        if (removeUnseen)
        {
            using var delete = _connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM records WHERE table_name = $table AND cycle < $cycle";
            delete.Parameters.AddWithValue("$table", table);
            delete.Parameters.AddWithValue("$cycle", state.Cycle);
            removed = delete.ExecuteNonQuery();
        }

        using (var complete = _connection.CreateCommand())
        {
            complete.Transaction = transaction;
            complete.CommandText = "UPDATE sync_state SET completed_at = $now, watermark = started_at WHERE table_name = $table";
            complete.Parameters.AddWithValue("$now", Format(now));
            complete.Parameters.AddWithValue("$table", table);
            complete.ExecuteNonQuery();
        }

        transaction.Commit();
        return removed;
    }

    public int Count(string table)
    {
        using var command = Command("SELECT COUNT(*) FROM records WHERE table_name = $table", ("$table", table));
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public IEnumerable<JsonObject> Read(string table)
    {
        using var command = Command("SELECT json FROM records WHERE table_name = $table ORDER BY id", ("$table", table));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            yield return (JsonObject)JsonNode.Parse(reader.GetString(0))!;
        }
    }

    public void Dispose() => _connection.Dispose();

    private static string Format(DateTimeOffset time) => time.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ReadTime(SqliteDataReader reader, int column) =>
        reader.IsDBNull(column)
            ? null
            : DateTimeOffset.Parse(reader.GetString(column), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
