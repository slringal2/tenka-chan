using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace TenkaChan.Mcp.Tracking;

public sealed record ReviewTarget(string Id, string SelectionId, string Kind, string ProjectId,
    string Project, string ProjectPath, string Name, string File, int Line,
    string SourceHash, string ContextHash, bool Trackable, string? UnavailableReason,
    [property: JsonIgnore] string Source)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReviewContextFingerprint? ContextFingerprint { get; init; }
    // Computed from the same loaded inputs by the unmodified v1 algorithm. Never persisted.
    [JsonIgnore] public string? LegacyContextHash { get; init; }
}
public sealed record ReviewEvidenceLink(string InstanceId, string CallId, string Tool,
    string Outcome, string? ObservedStatus, string RecordedUtc);
public sealed record ReviewCheckpoint(long Revision, string TargetId, string Kind, string Name,
    string ProjectPath, string Status, string SourceHash, string ContextHash,
    string Summary, string Agent, string SessionId, string RecordedUtc,
    IReadOnlyList<ReviewEvidenceLink> Evidence)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReviewContextFingerprint? ContextFingerprint { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReviewBenchmarkEvidence? Measurement { get; init; }
}
public sealed record ReviewState(string Status, string? PreviousStatus, string Reason,
    long Revision, bool SourceChanged, bool ContextChanged, bool RequiresReload,
    ReviewCheckpoint? Checkpoint)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReviewContextChange? ContextChange { get; init; }
    public ReviewPerformanceState Performance { get; init; } = ReviewPerformanceState.Unmeasured;
}
public sealed record ReviewQueueItem(ReviewTarget Target, ReviewState State);
public sealed record ReviewMarkRequest(string SnapshotId, string TargetId, string SourceHash,
    string ContextHash, long ExpectedRevision, string Status, string Summary,
    string[]? EvidenceCallIds = null)
{
    // Only the authenticated gateway's server-side measurement factory sets this.
    [JsonIgnore] public ReviewBenchmarkEvidence? Measurement { get; init; }
}
public sealed class ReviewConflictException(string message) : InvalidOperationException(message);

public static class ReviewProgress
{
    public const int MaxSourceCharacters = 200000;
    public const string Note = "チェック済み・最適化済みはエージェントの作業記録です。バグなし・速度改善の証明ではありません。型とメソッドの記録は独立しており、一括で完了扱いにはしません。";
    public static string Normalize(string source) => source.Replace("\r\n", "\n").Replace('\r', '\n');
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(text))));
    public static string CanonicalProject(string path)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }
    public static string TargetId(string project, string kind, string symbol) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("review-v1\0" + CanonicalProject(project) + "\0" + kind + "\0" + symbol)));
    public static ReviewState State(ReviewTarget target, ReviewCheckpoint? mark, bool stale)
    {
        var explanation = ReviewContextFingerprints.Explain(target, mark);
        if (mark is null) return new("unreviewed", null, stale ? "snapshot_outdated" : "not_checked", 0, false, false, stale, null)
        { ContextChange = explanation };
        var changed = target.SourceHash != mark.SourceHash;
        var unavailable = target.ContextFingerprint is { Complete: false };
        var context = unavailable || (target.ContextHash != mark.ContextHash && !ReviewContextFingerprints.MatchesLegacy(target, mark));
        var reason = stale ? "snapshot_outdated" : changed ? "source_changed" : unavailable ? "context_unavailable" : context ? "context_changed" : "current";
        var state = mark.Status == "unreviewed" ? "unreviewed" : stale || changed || context ? "needs_review" : mark.Status;
        return new(state, mark.Status, reason, mark.Revision, changed, context, stale, mark)
        { ContextChange = explanation, Performance = ReviewPerformanceState.From(mark.Measurement, stale || changed || context) };
    }

    public static void ValidateMark(ReviewTarget target, ReviewMarkRequest request, bool stale)
    {
        if (!target.Trackable) throw new InvalidOperationException(target.UnavailableReason);
        if (target.ContextFingerprint is { Complete: false })
            throw new InvalidOperationException("Review context is incomplete. Resolve references and reload before recording work.");
        if (stale) throw new ReviewConflictException("ソースが読み込み後に変更されました。プロジェクトを再読み込みしてから記録してください。");
        if (target.Id != request.TargetId || target.SourceHash != request.SourceHash || target.ContextHash != request.ContextHash)
            throw new ReviewConflictException("確認対象のコードまたは周辺コードが変わりました。tenka_review_targetで現在のハッシュを取得してください。");
        if (request.Status is not ("unreviewed" or "checked" or "optimized")) throw new ArgumentException("status must be unreviewed, checked or optimized.");
        if (request.Measurement is not null)
        {
            if (request.Status == "unreviewed") throw new ArgumentException("A reset must not attach a measurement.");
            ReviewBenchmarkMeasurements.ValidateAttachment(target, request.Measurement);
        }
        if (request.ExpectedRevision < 0) throw new ArgumentException("expectedRevision must be nonnegative.");
        if (string.IsNullOrWhiteSpace(request.Summary) || request.Summary.Length > 2000)
            throw new ArgumentException("何を確認・最適化したか、1～2000文字のsummaryを記録してください。");
        if (Hash(target.Source) != target.SourceHash) throw new InvalidDataException("記録するソースとハッシュが一致しません。");
        if (target.Source.Length > MaxSourceCharacters) throw new InvalidOperationException("対象のソースが保存上限を超えています。範囲を小さくしてください。");
    }
}
