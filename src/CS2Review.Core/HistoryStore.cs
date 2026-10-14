using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;

namespace CS2Review.Core;

public sealed class HistoryStore : IDisposable
{
    private readonly string path;
    private bool disposed;
    public HistoryStore(string path) => this.path = Path.GetFullPath(path);
    private SqliteConnection Open()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString());
        db.Open(); return db;
    }
    private static SqliteCommand Command(SqliteConnection db, string sql, params (string Key, object Value)[] parameters)
    {
        var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (key, value) in parameters) cmd.Parameters.AddWithValue(key, value);
        return cmd;
    }
    public Task InitializeAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested(); using var db = Open();
        using var tx = db.BeginTransaction();
        void Run(string sql) { ct.ThrowIfCancellationRequested(); using var cmd = Command(db, sql); cmd.Transaction = tx; cmd.ExecuteNonQuery(); ct.ThrowIfCancellationRequested(); }
        using var versionCmd = Command(db, "PRAGMA user_version"); versionCmd.Transaction = tx;
        var version = Convert.ToInt32(versionCmd.ExecuteScalar());
        if (version > 2) throw new InvalidDataException("历史数据库版本高于当前应用支持的版本。");
        using var exists = Command(db, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='matches'"); exists.Transaction = tx;
        var hasMatches = (long)exists.ExecuteScalar()! > 0;
        var migration = hasMatches && version < 2;
        if (migration)
            Run("ALTER TABLE matches RENAME TO legacy_matches; ALTER TABLE reports RENAME TO legacy_reports; DROP INDEX IF EXISTS report_lookup;");
        Run("""
            CREATE TABLE IF NOT EXISTS matches (id TEXT PRIMARY KEY, sha256 TEXT NOT NULL, schema_version INTEGER NOT NULL, parser_version TEXT NOT NULL, map TEXT NOT NULL, source TEXT NOT NULL, imported TEXT NOT NULL, player_count INTEGER NOT NULL, round_count INTEGER NOT NULL, json TEXT NOT NULL, UNIQUE(sha256,schema_version,parser_version));
            CREATE TABLE IF NOT EXISTS reports (id TEXT PRIMARY KEY, match_id TEXT NOT NULL REFERENCES matches(id), player_id TEXT NOT NULL, analysis_version TEXT NOT NULL, created TEXT NOT NULL, json TEXT NOT NULL);
            """);
        if (migration)
        {
            Run("""
                INSERT INTO matches(id,sha256,schema_version,parser_version,map,source,imported,player_count,round_count,json)
                    SELECT id,lower(sha256),COALESCE(json_extract(json,'$.schemaVersion'),1),COALESCE(json_extract(json,'$.parserVersion'),''),map,source,imported,player_count,round_count,json FROM legacy_matches;
                INSERT INTO reports SELECT * FROM legacy_reports;
                DROP TABLE legacy_reports;
                DROP TABLE legacy_matches;
                """);
        }
        Run("CREATE INDEX IF NOT EXISTS report_lookup ON reports(match_id,player_id,created); PRAGMA user_version=2;");
        ct.ThrowIfCancellationRequested(); tx.Commit();
    }, ct);
    public Task<string> SaveMatchAsync(MatchData match, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        if (match.SchemaVersion is not (1 or 2) || !Regex.IsMatch(match.SourceSha256, "^[a-fA-F0-9]{64}$") || match.Players.Count == 0 || !match.Rounds.Any(r => r.IsValid && r.Winner is "T" or "CT"))
            throw new ArgumentException("仅可保存带有效 SHA256 和完整有效回合的比赛。");
        using var db = Open(); using var tx = db.BeginTransaction();
        var sha = match.SourceSha256.ToLowerInvariant(); var parser = match.ParserVersion ?? "";
        using var select = Command(db, "SELECT id FROM matches WHERE sha256=$sha AND schema_version=$schema AND parser_version=$parser", ("$sha", sha), ("$schema", match.SchemaVersion), ("$parser", parser)); select.Transaction = tx;
        if (select.ExecuteScalar() is string existing) { ct.ThrowIfCancellationRequested(); tx.Commit(); return existing; }
        var id = match.Id;
        using (var collision = Command(db, "SELECT COUNT(*) FROM matches WHERE id=$id", ("$id", id ?? "")))
        {
            collision.Transaction = tx;
            if (string.IsNullOrWhiteSpace(id) || (long)collision.ExecuteScalar()! > 0)
                id = "version-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sha + "\0" + match.SchemaVersion + "\0" + parser))).ToLowerInvariant();
        }
        using (var insert = Command(db, "INSERT INTO matches(id,sha256,schema_version,parser_version,map,source,imported,player_count,round_count,json) VALUES($id,$sha,$schema,$parser,$map,$source,$imported,$players,$rounds,$json)", ("$id", id!), ("$sha", sha), ("$schema", match.SchemaVersion), ("$parser", parser), ("$map", match.MapName), ("$source", match.SourcePath), ("$imported", match.ImportedAt.ToString("O")), ("$players", match.Players.Count), ("$rounds", match.Rounds.Count(r => r.IsValid)), ("$json", JsonSerializer.Serialize(match, DataJson.Options))))
        { insert.Transaction = tx; insert.ExecuteNonQuery(); }
        ct.ThrowIfCancellationRequested(); tx.Commit(); return id!;
    }, ct);
    public Task<List<HistoryMatch>> ListMatchesAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var cmd = Command(db, "SELECT id,sha256,map,source,imported,player_count,round_count,schema_version,parser_version FROM matches ORDER BY imported DESC,id ASC"); using var reader = cmd.ExecuteReader();
        var rows = new List<HistoryMatch>();
        while (reader.Read()) { ct.ThrowIfCancellationRequested(); rows.Add(new() { Id = reader.GetString(0), SourceSha256 = reader.GetString(1), MapName = reader.GetString(2), SourcePath = reader.GetString(3), ImportedAt = DateTimeOffset.Parse(reader.GetString(4)), PlayerCount = reader.GetInt32(5), RoundCount = reader.GetInt32(6), SchemaVersion = reader.GetInt32(7), ParserVersion = reader.GetString(8) }); }
        return rows;
    }, ct);
    public Task<MatchData?> LoadMatchAsync(string id, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var cmd = Command(db, "SELECT json FROM matches WHERE id=$id", ("$id", id));
        var json = cmd.ExecuteScalar() as string; if (json is null) return null;
        ct.ThrowIfCancellationRequested(); var data = JsonSerializer.Deserialize<MatchData>(json, DataJson.Options)!; data.Id = id; return data;
    }, ct);
    public Task SaveReportAsync(string matchId, string playerId, AiReport report, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var tx = db.BeginTransaction();
        using (var saved = Command(db, "SELECT json FROM matches WHERE id=$match", ("$match", matchId)))
        {
            saved.Transaction = tx;
            var json = saved.ExecuteScalar() as string;
            var snapshot = json is null ? null : JsonSerializer.Deserialize<MatchData>(json, DataJson.Options);
            if (snapshot is null || !snapshot.Players.Any(p => p.Id == playerId))
                throw new InvalidDataException("报告所属比赛或玩家不存在于已保存比赛中。");
            snapshot.Id=matchId;
            if(report.RulesVersion!="rules-v1" && (report.PlayerId!=playerId || report.MatchId!=matchId || report.SourceSha256!=snapshot.SourceSha256 || report.AnalysisSnapshotSha256!=AnalysisBinding.Snapshot(snapshot,ct)))
                throw new InvalidDataException("报告来源与已保存比赛不一致，请重新分析当前快照。");
            if (report.Strengths is null || report.Priorities is null)
                throw new InvalidDataException("报告结构不完整，无法核对历史证据。");
            var events = snapshot.Rounds.Where(r => r.IsValid && r.Winner is "T" or "CT")
                .SelectMany(r => r.Events.Where(e => e.AfterRoundEnd != true && double.IsFinite(e.Time) && e.Time >= r.StartedAt && e.Time <= r.EndedAt).Select(e => (Round: r.Number, Event: e))).ToList();
            foreach (var item in report.Strengths.Concat(report.Priorities))
            {
                if (item is null || item.Evidence is null) throw new InvalidDataException("报告缺少可核对的历史证据结构。");
                foreach (var reference in item.Evidence)
                {
                    ct.ThrowIfCancellationRequested();
                    if (reference is null || string.IsNullOrWhiteSpace(reference.EventId) || !double.IsFinite(reference.Time) || !events.Any(x => x.Event.Id == reference.EventId && x.Round == reference.Round && Math.Abs(x.Event.Time - reference.Time) <= 0.0001))
                        throw new InvalidDataException("报告引用与已保存比赛快照不匹配；请加载已有比赛并重新分析。");
                }
            }
        }
        using var cmd = Command(db, "INSERT INTO reports VALUES($id,$match,$player,$version,$created,$json)", ("$id", report.Id), ("$match", matchId), ("$player", playerId), ("$version", report.AnalysisVersion), ("$created", report.CreatedAt.ToString("O")), ("$json", JsonSerializer.Serialize(report, DataJson.Options)));
        cmd.Transaction = tx; cmd.ExecuteNonQuery(); ct.ThrowIfCancellationRequested(); tx.Commit();
    }, ct);
    public Task<List<AiReport>> ListReportsAsync(string matchId, string playerId, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested(); using var db = Open(); using var cmd = Command(db, "SELECT json FROM reports WHERE match_id=$match AND player_id=$player ORDER BY created DESC", ("$match", matchId), ("$player", playerId)); using var reader = cmd.ExecuteReader(); var rows = new List<AiReport>();
        while (reader.Read()) { ct.ThrowIfCancellationRequested(); rows.Add(JsonSerializer.Deserialize<AiReport>(reader.GetString(0), DataJson.Options)!); } return rows;
    }, ct);
    public void Dispose() => disposed = true;
}
