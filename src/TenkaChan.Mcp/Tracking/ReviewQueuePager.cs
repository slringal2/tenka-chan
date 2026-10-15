using System.Text;
using System.Text.Json;

namespace TenkaChan.Mcp.Tracking;

public sealed record ReviewQueueSlice(int Total, int Offset, IReadOnlyList<ReviewQueueItem> Items,
    string? NextCursor, bool HasMore);

/// <summary>
/// Seeks after an immutable target identity, not an offset in the changing pending set.
/// A cursor is only a bounded navigation value, not an authorization credential or work lease.
/// The caller supplies already-authorized rows from one source snapshot and one filter scope.
/// </summary>
public static class ReviewQueuePager
{
    public static ReviewQueueSlice Page(string snapshotId, IReadOnlyList<ReviewQueueItem> scopedRows,
        string state, string? project, string kind, string? query, bool trackableOnly,
        int offset, int limit, string? cursor = null)
    {
        if (string.IsNullOrWhiteSpace(snapshotId) || snapshotId.Length > 100)
            throw new ArgumentException("A bounded source snapshot is required.");
        if (offset is < 0 or > 20000 || limit is < 1 or > 200)
            throw new ArgumentException("Invalid review page.");
        if (state is not ("all" or "pending" or "unreviewed" or "needs_review" or "checked" or "optimized"))
            throw new ArgumentException("Invalid review state.");
        if (cursor is not null && offset != 0)
            throw new ArgumentException("Use cursor OR offset, not both. Continuations use offset=0.");

        // Names/IDs belong to an immutable source snapshot. Review status must never be
        // part of this order: marking earlier entries must not move the pagination boundary.
        var ordered = scopedRows.OrderBy(r => r.Target.Project, StringComparer.Ordinal)
            .ThenBy(r => r.Target.ProjectPath, StringComparer.Ordinal)
            .ThenBy(r => r.Target.Name, StringComparer.Ordinal)
            .ThenBy(r => r.Target.Kind, StringComparer.Ordinal)
            .ThenBy(r => r.Target.Id, StringComparer.Ordinal).ToArray();
        var scope = ReviewProgress.Hash(JsonSerializer.Serialize(new[] {
            snapshotId, project ?? "", kind, query ?? "", state, trackableOnly ? "1" : "0" }));
        var start = 0;
        if (cursor is not null)
        {
            var afterId = Decode(cursor, snapshotId, scope);
            var found = Array.FindIndex(ordered, r => r.Target.Id == afterId);
            if (found < 0) throw new ReviewConflictException("確認一覧の続きが無効です。同じ条件で先頭から取得してください。");
            start = found + 1; // The anchor need not still match the state filter.
        }
        bool Matches(ReviewQueueItem row) => (!trackableOnly || row.Target.Trackable) &&
            (state == "all" || (state == "pending"
                ? row.State.Status is "unreviewed" or "needs_review" : row.State.Status == state));
        var total = ordered.Count(Matches);
        var remaining = ordered.Skip(start).Where(Matches).Skip(cursor is null ? offset : 0)
            .Take(limit + 1).ToArray();
        var items = remaining.Take(limit).ToArray();
        var more = remaining.Length > limit;
        var next = more ? Encode(snapshotId, scope, items[^1].Target.Id) : null;
        return new(total, cursor is null ? offset : 0, items, next, more);
    }

    private static string Encode(string snapshot, string scope, string afterId) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes("1\n" + snapshot + "\n" + scope + "\n" + afterId))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Decode(string cursor, string snapshot, string scope)
    {
        if (cursor.Length is 0 or > 512 || cursor.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new ArgumentException("Invalid review continuation cursor.");
        string[] parts;
        try
        {
            var base64 = cursor.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight((base64.Length + 3) / 4 * 4, '=');
            parts = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(base64)).Split('\n');
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        { throw new ArgumentException("Invalid review continuation cursor.", ex); }
        if (parts.Length != 4 || parts[0] != "1" || parts[3].Length != 64 || !parts[3].All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid review continuation cursor.");
        if (parts[1] != snapshot || parts[2] != scope)
            throw new ReviewConflictException("ソースまたは絞り込み条件が変わりました。cursorを外して先頭から取得してください。");
        return parts[3];
    }
}
