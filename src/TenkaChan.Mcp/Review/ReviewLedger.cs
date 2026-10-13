using Microsoft.Data.Sqlite;

namespace TenkaChan.Mcp.Review;

public sealed record ReviewHypothesis(
    string Id, string SnapshotId, string MethodId, string Category,
    string Claim, string FailureCondition, string Status,
    string CreatedUtc, string UpdatedUtc, string? VerdictRationale);

public sealed record ReviewEvidence(
    string Id, string HypothesisId, string Kind, string Summary, string Provenance,
    string? ArtifactId, string CreatedUtc);

public sealed record ReviewCase(
    ReviewHypothesis Hypothesis, IReadOnlyList<ReviewEvidence> Evidence);

/// <summary>
/// Persist hypotheses, their oracle provenance and observed evidence across
/// MCP client restarts. This ledger deliberately never claims that an LLM
/// provided assertion is proof of a defect.
/// </summary>
public sealed class ReviewLedger
{
    private static readonly HashSet<string> AllowedCategories = new(StringComparer.Ordinal)
    {
        "correctness", "performance", "allocation", "concurrency", "security",
        "contract", "resource", "other"
    };

    private static readonly HashSet<string> AllowedEvidenceKinds = new(StringComparer.Ordinal)
    {
        "static_analysis", "contract", "test_execution", "benchmark",
        "profiler", "source_inspection", "human_review"
    };

    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        "suspected", "needs_review", "reported_reproduced", "not_reproduced", "refuted"
    };

    private readonly string _connectionString;

    public ReviewLedger()
        : this(Path.Combine(
            Path.GetFullPath(Environment.GetEnvironmentVariable("TENKA_DATA_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "tenka-chan")),
            "review-ledger.sqlite"))
    {
    }

    public ReviewLedger(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath)) throw new ArgumentException("Database path is required.");
        var fullPath = Path.GetFullPath(dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();
        using var conn = Connect();
        using var command = conn.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS hypotheses (
                id TEXT PRIMARY KEY,
                snapshot_id TEXT NOT NULL,
                method_id TEXT NOT NULL,
                category TEXT NOT NULL,
                claim TEXT NOT NULL,
                failure_condition TEXT NOT NULL,
                status TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                verdict_rationale TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_hypotheses_status ON hypotheses(status, created_utc);
            CREATE TABLE IF NOT EXISTS evidence (
                id TEXT PRIMARY KEY,
                hypothesis_id TEXT NOT NULL REFERENCES hypotheses(id),
                kind TEXT NOT NULL,
                summary TEXT NOT NULL,
                provenance TEXT NOT NULL,
                artifact_id TEXT,
                created_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_evidence_hypothesis ON evidence(hypothesis_id, created_utc);
            """;
        command.ExecuteNonQuery();
    }

    public ReviewHypothesis Create(string snapshotId, string methodId, string category,
        string claim, string failureCondition)
    {
        ValidateString(snapshotId, 128, nameof(snapshotId));
        ValidateString(methodId, 512, nameof(methodId));
        ValidateString(claim, 8000, nameof(claim));
        ValidateString(failureCondition, 8000, nameof(failureCondition));
        if (!AllowedCategories.Contains(category)) throw new ArgumentException("Unknown category.");
        var id = Guid.NewGuid().ToString("N");
        var time = DateTimeOffset.UtcNow.ToString("O");
        using var conn = Connect();
        using var command = conn.CreateCommand();
        command.CommandText = """
            INSERT INTO hypotheses (id, snapshot_id, method_id, category, claim,
                                    failure_condition, status, created_utc, updated_utc)
            VALUES ($id, $snapshot, $method, $category, $claim, $condition,
                    'suspected', $created, $updated)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$snapshot", snapshotId);
        command.Parameters.AddWithValue("$method", methodId);
        command.Parameters.AddWithValue("$category", category);
        command.Parameters.AddWithValue("$claim", claim);
        command.Parameters.AddWithValue("$condition", failureCondition);
        command.Parameters.AddWithValue("$created", time);
        command.Parameters.AddWithValue("$updated", time);
        command.ExecuteNonQuery();
        return new ReviewHypothesis(id, snapshotId, methodId, category,
            claim, failureCondition, "suspected", time, time, null);
    }

    public ReviewEvidence AddEvidence(string hypothesisId, string kind,
        string summary, string provenance, string? artifactId = null)
    {
        ValidateIdentifier(hypothesisId, nameof(hypothesisId));
        if (!AllowedEvidenceKinds.Contains(kind)) throw new ArgumentException("Unknown evidence kind.");
        ValidateString(summary, 10000, nameof(summary));
        ValidateString(provenance, 1024, nameof(provenance));
        if (artifactId is not null) ValidateString(artifactId, 1024, nameof(artifactId));
        var id = Guid.NewGuid().ToString("N");
        var time = DateTimeOffset.UtcNow.ToString("O");

        using var conn = Connect();
        using var command = conn.CreateCommand();
        command.CommandText = """
            INSERT INTO evidence
                (id, hypothesis_id, kind, summary, provenance, artifact_id, created_utc)
            VALUES ($id, $hypothesis, $kind, $summary, $provenance, $artifact, $created)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$hypothesis", hypothesisId);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$summary", summary);
        command.Parameters.AddWithValue("$provenance", provenance);
        command.Parameters.AddWithValue("$artifact", (object?)artifactId ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", time);
        command.ExecuteNonQuery(); // FK enforces that hypothesis exists.
        return new ReviewEvidence(id, hypothesisId, kind, summary, provenance, artifactId, time);
    }

    public ReviewHypothesis SetStatus(string hypothesisId, string status, string rationale)
    {
        ValidateIdentifier(hypothesisId, nameof(hypothesisId));
        if (!AllowedStatuses.Contains(status)) throw new ArgumentException("Unknown status.");
        ValidateString(rationale, 5000, nameof(rationale));

        using var conn = Connect();
        using var transaction = conn.BeginTransaction();
        var found = ReadHypothesis(conn, hypothesisId, transaction);
        if (found is null) throw new KeyNotFoundException("Hypothesis not found.");
        if (status == "reported_reproduced")
        {
            using var evidence = conn.CreateCommand();
            evidence.Transaction = transaction;
            evidence.CommandText = """
                SELECT kind, provenance FROM evidence WHERE hypothesis_id = $id
                """;
            evidence.Parameters.AddWithValue("$id", hypothesisId);
            using var reader = evidence.ExecuteReader();
            var test = false;
            var oracle = false;
            while (reader.Read())
            {
                var kind = reader.GetString(0);
                var source = reader.GetString(1);
                test |= kind == "test_execution";
                oracle |= kind == "contract" &&
                    !source.StartsWith("llm:", StringComparison.OrdinalIgnoreCase);
            }
            if (!test || !oracle)
                throw new InvalidOperationException(
                    "A reported reproduction requires a test_execution and an independently sourced contract. "
                    + "This status is still not a formal proof.");
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        using (var command = conn.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE hypotheses SET status = $status, updated_utc = $now,
                    verdict_rationale = $rationale WHERE id = $id
                """;
            command.Parameters.AddWithValue("$status", status);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$rationale", rationale);
            command.Parameters.AddWithValue("$id", hypothesisId);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return found with { Status = status, UpdatedUtc = now, VerdictRationale = rationale };
    }

    public ReviewCase Get(string hypothesisId)
    {
        ValidateIdentifier(hypothesisId, nameof(hypothesisId));
        using var conn = Connect();
        var hypothesis = ReadHypothesis(conn, hypothesisId)
            ?? throw new KeyNotFoundException("Hypothesis not found.");
        using var command = conn.CreateCommand();
        command.CommandText = """
            SELECT id, hypothesis_id, kind, summary, provenance, artifact_id, created_utc
            FROM evidence WHERE hypothesis_id = $id ORDER BY created_utc, id
            """;
        command.Parameters.AddWithValue("$id", hypothesisId);
        using var reader = command.ExecuteReader();
        var records = new List<ReviewEvidence>();
        while (reader.Read())
            records.Add(new ReviewEvidence(reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6)));
        return new ReviewCase(hypothesis, records);
    }

    public IReadOnlyList<ReviewHypothesis> List(string? status, int limit = 100)
    {
        if (status is not null && !AllowedStatuses.Contains(status))
            throw new ArgumentException("Unknown status.");
        using var conn = Connect();
        using var command = conn.CreateCommand();
        command.CommandText = """
            SELECT id, snapshot_id, method_id, category, claim, failure_condition,
                   status, created_utc, updated_utc, verdict_rationale
            FROM hypotheses
            WHERE ($status IS NULL OR status = $status)
            ORDER BY updated_utc DESC, id DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        using var reader = command.ExecuteReader();
        var results = new List<ReviewHypothesis>();
        while (reader.Read()) results.Add(MapHypothesis(reader));
        return results;
    }

    private static ReviewHypothesis? ReadHypothesis(
        SqliteConnection conn, string id, SqliteTransaction? transaction = null)
    {
        using var command = conn.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, snapshot_id, method_id, category, claim, failure_condition,
                   status, created_utc, updated_utc, verdict_rationale
            FROM hypotheses WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapHypothesis(reader) : null;
    }

    private static ReviewHypothesis MapHypothesis(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
            reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9));

    private SqliteConnection Connect()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    private static void ValidateString(string value, int maxLength, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            throw new ArgumentException($"{field} must have 1 to {maxLength} nonempty characters.");
    }

    private static void ValidateIdentifier(string id, string field)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new ArgumentException($"Invalid {field}.");
    }
}
