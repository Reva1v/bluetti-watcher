using System.Text.Json;
using BluettiWatcher.Decision;
using Microsoft.Data.Sqlite;

namespace BluettiWatcher.Storage;

internal enum ReadingStatus { Accepted = 0, Missed = 1, Rejected = 2 }

/// <summary>A history row as the page reads it (raw, or an average over a bucket).</summary>
internal sealed record HistoryRow(long At, double? Soc, double? InAc, double? InDc, double? OutAc, double? OutDc, double? Volt, double? Hz, double Mains, string? Mode);

internal sealed record EventRow(long At, string Kind, string Detail);

/// <summary>
/// station.db: one row per reading (accepted, missed or rejected with its reason), raw rows kept RawRetentionDays, then
/// folded into 5-minute aggregates kept forever; events; the decision state; meta. Times are Unix seconds.
/// </summary>
internal sealed class StationStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly Lock _gate = new();

    public StationStore(string path)
    {
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        using var ddl = _db.CreateCommand();
        ddl.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA cache_size=-512;
            CREATE TABLE IF NOT EXISTS reading(at INTEGER NOT NULL, status INTEGER NOT NULL, soc REAL, in_ac REAL, in_dc REAL,
              out_ac REAL, out_dc REAL, volt REAL, hz REAL, mode TEXT, reason TEXT);
            CREATE INDEX IF NOT EXISTS reading_at ON reading(at);
            CREATE TABLE IF NOT EXISTS agg5(at INTEGER PRIMARY KEY, n INTEGER NOT NULL, soc REAL, soc_min REAL, soc_max REAL,
              in_ac REAL, in_dc REAL, out_ac REAL, out_dc REAL, volt REAL, hz REAL, mains REAL, missed INTEGER NOT NULL, rejected INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS event(at INTEGER NOT NULL, kind TEXT NOT NULL, detail TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS event_at ON event(at);
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """;
        ddl.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();

    private static object Db(double? v) => v is { } x ? x : DBNull.Value;
    private static object Db(string? v) => v is null ? DBNull.Value : v;
    private static double? D(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetDouble(i);

    public void AddReading(DateTimeOffset at, Reading? r, ReadingStatus status, string reason)
    {
        lock (_gate) AddReadingCore(null, at.ToUnixTimeSeconds(), r, status, reason);
    }

    private void AddReadingCore(SqliteTransaction? tx, long at, Reading? r, ReadingStatus status, string reason)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO reading(at,status,soc,in_ac,in_dc,out_ac,out_dc,volt,hz,mode,reason) VALUES($at,$st,$soc,$ia,$id,$oa,$od,$v,$hz,$m,$why)";
        cmd.Parameters.AddWithValue("$at", at);
        cmd.Parameters.AddWithValue("$st", (int)status);
        cmd.Parameters.AddWithValue("$soc", Db(r?.Soc));
        cmd.Parameters.AddWithValue("$ia", Db(r?.InAc));
        cmd.Parameters.AddWithValue("$id", Db(r?.InDc));
        cmd.Parameters.AddWithValue("$oa", Db(r?.OutAc));
        cmd.Parameters.AddWithValue("$od", Db(r?.OutDc));
        cmd.Parameters.AddWithValue("$v", Db(r?.Volt));
        cmd.Parameters.AddWithValue("$hz", Db(r?.Hz));
        cmd.Parameters.AddWithValue("$m", Db(r?.Mode));
        cmd.Parameters.AddWithValue("$why", reason.Length == 0 ? DBNull.Value : reason);
        cmd.ExecuteNonQuery();
    }

    public void AddEvent(DateTimeOffset at, string kind, string detail)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO event(at,kind,detail) VALUES($at,$k,$d)";
            cmd.Parameters.AddWithValue("$at", at.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$d", detail);
            cmd.ExecuteNonQuery();
        }
    }

    public string? GetMeta(string key)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE key=$k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string;
        }
    }

    public void SetMeta(string key, string value)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    public StationState LoadState()
    {
        string? json = GetMeta("state");
        if (json is null) return new StationState();
        try { return StationState.Normalize(JsonSerializer.Deserialize(json, StationJson.Default.StationState)); }
        catch (JsonException) { return new StationState(); }   // a damaged state starts fresh
    }

    public void SaveState(StationState s) => SetMeta("state", JsonSerializer.Serialize(s, StationJson.Default.StationState));

    /// <summary>Raw rows older than the retention folded into 5-minute aggregates, then deleted. Returns the raw rows folded.</summary>
    public int Compact(DateTimeOffset now, int retentionDays)
    {
        long cutoff = (now.AddDays(-retentionDays).ToUnixTimeSeconds() / 300) * 300;
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                // A bucket already folded (a late row) is merged by weight, never overwritten.
                cmd.CommandText = """
                    INSERT INTO agg5(at,n,soc,soc_min,soc_max,in_ac,in_dc,out_ac,out_dc,volt,hz,mains,missed,rejected)
                    SELECT (at/300)*300 AS b,
                      SUM(status=0),
                      AVG(CASE WHEN status=0 THEN soc END), MIN(CASE WHEN status=0 THEN soc END), MAX(CASE WHEN status=0 THEN soc END),
                      AVG(CASE WHEN status=0 THEN in_ac END), AVG(CASE WHEN status=0 THEN in_dc END),
                      AVG(CASE WHEN status=0 THEN out_ac END), AVG(CASE WHEN status=0 THEN out_dc END),
                      AVG(CASE WHEN status=0 THEN volt END), AVG(CASE WHEN status=0 THEN hz END),
                      AVG(CASE WHEN status=0 THEN (in_ac > 0) END),
                      SUM(status=1), SUM(status=2)
                    FROM reading WHERE at < $cut GROUP BY b
                    ON CONFLICT(at) DO UPDATE SET
                      soc = CASE WHEN n + excluded.n = 0 THEN NULL ELSE (COALESCE(soc,0)*n + COALESCE(excluded.soc,0)*excluded.n) / (n + excluded.n) END,
                      soc_min = MIN(COALESCE(soc_min, excluded.soc_min), COALESCE(excluded.soc_min, soc_min)),
                      soc_max = MAX(COALESCE(soc_max, excluded.soc_max), COALESCE(excluded.soc_max, soc_max)),
                      in_ac = CASE WHEN n + excluded.n = 0 THEN NULL ELSE (COALESCE(in_ac,0)*n + COALESCE(excluded.in_ac,0)*excluded.n) / (n + excluded.n) END,
                      in_dc = CASE WHEN n + excluded.n = 0 THEN NULL ELSE (COALESCE(in_dc,0)*n + COALESCE(excluded.in_dc,0)*excluded.n) / (n + excluded.n) END,
                      out_ac = CASE WHEN n + excluded.n = 0 THEN NULL ELSE (COALESCE(out_ac,0)*n + COALESCE(excluded.out_ac,0)*excluded.n) / (n + excluded.n) END,
                      out_dc = CASE WHEN n + excluded.n = 0 THEN NULL ELSE (COALESCE(out_dc,0)*n + COALESCE(excluded.out_dc,0)*excluded.n) / (n + excluded.n) END,
                      volt = CASE WHEN n + excluded.n = 0 THEN NULL ELSE (COALESCE(volt,0)*n + COALESCE(excluded.volt,0)*excluded.n) / (n + excluded.n) END,
                      hz = CASE WHEN n + excluded.n = 0 THEN NULL ELSE (COALESCE(hz,0)*n + COALESCE(excluded.hz,0)*excluded.n) / (n + excluded.n) END,
                      mains = CASE WHEN n + excluded.n = 0 THEN NULL ELSE (COALESCE(mains,0)*n + COALESCE(excluded.mains,0)*excluded.n) / (n + excluded.n) END,
                      n = n + excluded.n, missed = missed + excluded.missed, rejected = rejected + excluded.rejected
                    """;
                cmd.Parameters.AddWithValue("$cut", cutoff);
                cmd.ExecuteNonQuery();
            }
            int deleted;
            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM reading WHERE at < $cut";
                cmd.Parameters.AddWithValue("$cut", cutoff);
                deleted = cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return deleted;
        }
    }

    /// <summary>
    /// Accepted readings in [from, to): raw rows and 5-minute aggregates, averaged per <paramref name="step"/> seconds
    /// (step 1 = each row as stored). Weighted by the readings each row stands for.
    /// </summary>
    public List<HistoryRow> History(long from, long to, long step, int limit = 20000)
    {
        step = Math.Max(1, step);
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT (at/$s)*$s AS b,
                  SUM(n*soc)/SUM(CASE WHEN soc IS NULL THEN 0 ELSE n END),
                  SUM(n*in_ac)/SUM(CASE WHEN in_ac IS NULL THEN 0 ELSE n END),
                  SUM(n*in_dc)/SUM(CASE WHEN in_dc IS NULL THEN 0 ELSE n END),
                  SUM(n*out_ac)/SUM(CASE WHEN out_ac IS NULL THEN 0 ELSE n END),
                  SUM(n*out_dc)/SUM(CASE WHEN out_dc IS NULL THEN 0 ELSE n END),
                  SUM(n*volt)/SUM(CASE WHEN volt IS NULL THEN 0 ELSE n END),
                  SUM(n*hz)/SUM(CASE WHEN hz IS NULL THEN 0 ELSE n END),
                  SUM(n*mains)/SUM(n),
                  MAX(mode)
                FROM (
                  SELECT at, 1 AS n, soc, in_ac, in_dc, out_ac, out_dc, volt, hz, (in_ac > 0) * 1.0 AS mains, mode
                    FROM reading WHERE status=0 AND at >= $f AND at < $t
                  UNION ALL
                  SELECT at, n, soc, in_ac, in_dc, out_ac, out_dc, volt, hz, mains, NULL
                    FROM agg5 WHERE n > 0 AND at >= $f AND at < $t)
                GROUP BY b ORDER BY b DESC LIMIT $lim
                """;
            cmd.Parameters.AddWithValue("$s", step);
            cmd.Parameters.AddWithValue("$f", from);
            cmd.Parameters.AddWithValue("$t", to);
            cmd.Parameters.AddWithValue("$lim", limit);
            var rows = new List<HistoryRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add(new HistoryRow(r.GetInt64(0), D(r, 1), D(r, 2), D(r, 3), D(r, 4), D(r, 5), D(r, 6), D(r, 7), D(r, 8) ?? 0, r.IsDBNull(9) ? null : r.GetString(9)));
            rows.Reverse();
            return rows;
        }
    }

    /// <summary>Missed and rejected readings and the service's events in [from, to), oldest first.</summary>
    public List<EventRow> Events(long from, long to, int limit = 2000)
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = """
                SELECT at, kind, detail FROM (
                  SELECT at, CASE status WHEN 1 THEN 'missed' ELSE 'rejected' END AS kind, COALESCE(reason, 'no answer') AS detail
                    FROM reading WHERE status <> 0 AND at >= $f AND at < $t
                  UNION ALL
                  SELECT at, kind, detail FROM event WHERE at >= $f AND at < $t)
                ORDER BY at DESC LIMIT $lim
                """;
            cmd.Parameters.AddWithValue("$f", from);
            cmd.Parameters.AddWithValue("$t", to);
            cmd.Parameters.AddWithValue("$lim", limit);
            var rows = new List<EventRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add(new EventRow(r.GetInt64(0), r.GetString(1), r.GetString(2)));
            rows.Reverse();
            return rows;
        }
    }

    /// <summary>The connection's page cache setting (negative = KiB), for tests.</summary>
    internal long CacheSize()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "PRAGMA cache_size";
            return (long)cmd.ExecuteScalar()!;
        }
    }

    /// <summary>Row counts (raw readings, aggregates), for health and tests.</summary>
    public (int Raw, int Agg) Counts()
    {
        lock (_gate)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT (SELECT COUNT(*) FROM reading), (SELECT COUNT(*) FROM agg5)";
            using var r = cmd.ExecuteReader();
            r.Read();
            return (r.GetInt32(0), r.GetInt32(1));
        }
    }
}
