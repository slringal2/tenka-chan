using System.Text.Json;

namespace TenkaChan.Mcp.Review;

/// <summary>
/// Persist progress snapshots after every method. This is not a background
/// service: a stopped process marks unfinished runs as interrupted.
/// </summary>
public sealed class ReviewRunStore
{
    private readonly string _directory;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ReviewRunStore()
        : this(Path.Combine(
            Path.GetFullPath(Environment.GetEnvironmentVariable("TENKA_DATA_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "tenka-chan")),
            "review-runs")) { }

    public ReviewRunStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    public void Save(AutonomousReviewStatus status)
    {
        RequireId(status.RunId);
        var destination = Path.Combine(_directory, status.RunId + ".json");
        var temporary = Path.Combine(_directory, "." + status.RunId + "." +
            Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(status, SerializerOptions));
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public AutonomousReviewStatus Get(string id)
    {
        RequireId(id);
        var path = Path.Combine(_directory, id + ".json");
        if (!File.Exists(path)) throw new KeyNotFoundException("Review run not found.");
        if (new FileInfo(path).Length > 400_000)
            throw new InvalidDataException("Review run snapshot too large.");
        var status = JsonSerializer.Deserialize<AutonomousReviewStatus>(
            File.ReadAllText(path), SerializerOptions)
            ?? throw new InvalidDataException("Review run snapshot invalid.");
        if (status.RunId != id)
            throw new InvalidDataException("Review run ID mismatch.");
        if (status.Status is "running" or "queued")
            return status with { Status = "interrupted",
                FinishedUtc = DateTimeOffset.UtcNow.ToString("O") };
        return status;
    }

    public IReadOnlyList<AutonomousReviewStatus> List(int limit = 20)
    {
        var result = new List<AutonomousReviewStatus>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json")
            .OrderByDescending(File.GetLastWriteTimeUtc).Take(Math.Clamp(limit, 1, 100) * 3))
        {
            if (result.Count >= Math.Clamp(limit, 1, 100)) break;
            var name = Path.GetFileNameWithoutExtension(path);
            if (!Guid.TryParseExact(name, "N", out _)) continue;
            try { result.Add(Get(name)); }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { }
        }
        return result.OrderByDescending(x => x.StartedUtc, StringComparer.Ordinal).ToArray();
    }

    private static void RequireId(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new ArgumentException("Expected a run UUID.");
    }
}
