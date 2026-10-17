using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace TenkaChan.Mcp.Tracking;

/// <summary>Append-only checkpoints. Stable symbol identity, content hashes and optimistic concurrency.
/// A status is agent-reported work, never a verified performance claim. No source-tree writes.</summary>
public sealed class ReviewProgressStore
{
    private readonly string _connectionString;
    private readonly object _latestLock = new();
    private long _latestRevision = -1;
    private IReadOnlyDictionary<string, ReviewCheckpoint>? _latestCache;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public ReviewProgressStore() : this(Path.Combine(Path.GetFullPath(
        Environment.GetEnvironmentVariable("TENKA_DATA_DIR") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tenka-chan")), "review-progress.sqlite")) { }
    public ReviewProgressStore(string path)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 5 }.ToString();
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS review_checkpoints (
                revision INTEGER PRIMARY KEY AUTOINCREMENT,
                target_id TEXT NOT NULL, payload TEXT NOT NULL, source TEXT NOT NULL, source_chars INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS review_target_revision ON review_checkpoints(target_id, revision DESC);
            """;
        cmd.ExecuteNonQuery();
    }
    private SqliteConnection Open() { var db = new SqliteConnection(_connectionString); db.Open(); return db; }
    public IReadOnlyDictionary<string, ReviewCheckpoint> Latest()
    {
        // Check the append-only revision inside the same read transaction as the payload.
        // Another process's committed append is detected without decoding every row on every poll.
        lock (_latestLock)
        {
            using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
            using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "SELECT COALESCE(MAX(revision),0) FROM review_checkpoints";
            var revision = Convert.ToInt64(cmd.ExecuteScalar());
            if (_latestCache is not null && _latestRevision == revision)
            { tx.Commit(); return _latestCache; }
            cmd.CommandText = """
                SELECT c.revision,c.payload FROM review_checkpoints c
                JOIN (SELECT target_id,MAX(revision) AS rev FROM review_checkpoints GROUP BY target_id) x
                    ON c.target_id=x.target_id AND c.revision=x.rev;
                """;
            var rows = new Dictionary<string, ReviewCheckpoint>(StringComparer.Ordinal);
            using (var reader = cmd.ExecuteReader())
                while (reader.Read()) { var row = Decode(reader); rows.Add(row.TargetId, row); }
            tx.Commit();
            _latestRevision = revision;
            return _latestCache = new System.Collections.ObjectModel.ReadOnlyDictionary<string, ReviewCheckpoint>(rows);
        }
    }
    public IReadOnlyList<ReviewCheckpoint> History(string targetId, int offset = 0, int limit = 50)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT revision,payload FROM review_checkpoints WHERE target_id=$id ORDER BY revision DESC LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$id", targetId); cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        cmd.Parameters.AddWithValue("$offset", Math.Clamp(offset, 0, 20000));
        using var reader = cmd.ExecuteReader(); var rows = new List<ReviewCheckpoint>();
        while (reader.Read()) rows.Add(Decode(reader)); return rows;
    }
    public (ReviewCheckpoint Checkpoint, string Source) Read(string targetId, long revision)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT revision,payload,source FROM review_checkpoints WHERE target_id=$id AND revision=$revision";
        cmd.Parameters.AddWithValue("$id", targetId); cmd.Parameters.AddWithValue("$revision", revision);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new KeyNotFoundException("この対象の確認履歴ではありません。");
        var mark = Decode(reader); var source = reader.GetString(2);
        if (ReviewProgress.Hash(source) != mark.SourceHash) throw new InvalidDataException("保存された確認履歴のソースハッシュが一致しません。");
        return (mark, source);
    }
    public ReviewCheckpoint Save(ReviewTarget target, ReviewMarkRequest request, string agent,
        string session, IReadOnlyList<ReviewEvidenceLink> evidence)
    {
        ReviewProgress.ValidateMark(target, request, false);
        if (agent.Length > 100 || evidence.Count > 10) throw new ArgumentException("Review attribution exceeds budget.");
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(MAX(revision),0) FROM review_checkpoints WHERE target_id=$id";
        cmd.Parameters.AddWithValue("$id", target.Id);
        var current = Convert.ToInt64(cmd.ExecuteScalar());
        if (current != request.ExpectedRevision)
            throw new ReviewConflictException("別のエージェントが記録を更新しました。履歴を取得し直してください。上書きはしていません。");
        cmd.Parameters.Clear();
        cmd.CommandText = "SELECT COUNT(*),COALESCE(SUM(source_chars),0) FROM review_checkpoints";
        using (var counts = cmd.ExecuteReader())
        {
            counts.Read();
            if (counts.GetInt64(0) >= 20000 || counts.GetInt64(1) + target.Source.Length > 64000000)
                throw new InvalidOperationException("確認履歴の保存上限です。既存履歴は削除していません。データを保全してから管理してください。");
        }
        var record = new ReviewCheckpoint(0, target.Id, target.Kind, target.Name, target.ProjectPath,
            request.Status, target.SourceHash, target.ContextHash, request.Summary.Trim(), agent, session,
            DateTimeOffset.UtcNow.ToString("O"), evidence)
        { ContextFingerprint = target.ContextFingerprint, Measurement = request.Measurement };
        cmd.CommandText = "INSERT INTO review_checkpoints(target_id,payload,source,source_chars) VALUES($id,$payload,$source,$size); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$id", target.Id); cmd.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(record, Json));
        cmd.Parameters.AddWithValue("$source", target.Source); cmd.Parameters.AddWithValue("$size", target.Source.Length);
        var revision = Convert.ToInt64(cmd.ExecuteScalar()); tx.Commit();
        lock (_latestLock) { _latestCache = null; _latestRevision = -1; }
        return record with { Revision = revision };
    }
    private static ReviewCheckpoint Decode(SqliteDataReader reader) =>
        (JsonSerializer.Deserialize<ReviewCheckpoint>(reader.GetString(1), Json)
            ?? throw new InvalidDataException("Invalid review checkpoint.")) with { Revision = reader.GetInt64(0) };
}
