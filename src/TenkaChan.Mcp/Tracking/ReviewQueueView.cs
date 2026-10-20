namespace TenkaChan.Mcp.Tracking;

public sealed record ReviewQueueCounts(int Total, int Unreviewed, int NeedsReview,
    int CheckedCount, int Optimized, int Untrackable, int BlockedPending)
{
    public int Pending => Unreviewed + NeedsReview;
}
public sealed record ReviewQueueRestart(string SnapshotId, string State = "pending", string Kind = "all",
    string? ProjectId = null, string? Query = null, bool TrackableOnly = false, int Offset = 0, string? Cursor = null);
public sealed record ReviewQueueCoverage(ReviewQueueCounts LoadedIndex, ReviewQueueCounts Methods,
    ReviewQueueCounts Types, int PendingOutsideScope, int PendingHiddenByFilters,
    bool HasPendingInLoadedIndex, bool RequiresReloadInLoadedIndex, string Status,
    ReviewQueueRestart Restart, string NextAction);
public sealed record ReviewQueueView(string SnapshotId, int Total, int Offset,
    IReadOnlyList<ReviewQueueItem> Items, string? NextCursor, bool HasMore,
    string Ordering, ReviewQueueCounts Summary, bool RequiresReload, bool Limited,
    string Note, string PagingNote, string Scope, ReviewQueueCoverage Coverage);

/// <summary>
/// Pure response shaping, shared by HTTP and MCP and testable without DashboardState.
/// Pagination and scoped summary keep their existing semantics. Coverage is always
/// computed BEFORE kind/project/query/state/trackable filters and BEFORE pagination.
/// Nothing in this response promotes an agent checkpoint to verified work.
/// </summary>
public static class ReviewQueueViews
{
    public const string DefaultKind = "all";
    public static ReviewQueueView Build(string snapshotId, IReadOnlyList<ReviewQueueItem> allRows,
        string? project = null, string? kind = null, string state = "pending", string? query = null,
        int offset = 0, int limit = 50, string? cursor = null, bool trackableOnly = false, bool limited = false)
    {
        ArgumentNullException.ThrowIfNull(allRows);
        kind ??= DefaultKind;
        if (kind is not ("all" or "type" or "method"))
            throw new ArgumentException(
                "Invalid review kind. kind=all|type|method (category of target); " +
                "use state=pending|all|unreviewed|needs_review|checked|optimized for review status. " +
                "Do not pass pending as kind.");
        if (state is not ("all" or "pending" or "unreviewed" or "needs_review" or "checked" or "optimized"))
            throw new ArgumentException(
                "Invalid review state. Use state=pending|all|unreviewed|needs_review|checked|optimized. " +
                "kind is separately all|type|method.");
        if ((query?.Length ?? 0) > 200) throw new ArgumentException("Invalid review page.");
        var scoped = allRows.Where(r => (kind == "all" || r.Target.Kind == kind) &&
            (string.IsNullOrEmpty(project) || r.Target.ProjectId == project) &&
            (string.IsNullOrEmpty(query) || r.Target.Name.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        var page = ReviewQueuePager.Page(snapshotId, scoped, state, project, kind, query, trackableOnly, offset, limit, cursor);
        var total = Count(allRows);
        var pendingInScope = scoped.Count(Pending);
        var visiblePending = scoped.Count(r => Pending(r) && (!trackableOnly || r.Target.Trackable) &&
            (state is "all" or "pending" || r.State.Status == state));
        var reload = allRows.Any(r => r.State.RequiresReload);
        var status = reload ? "reload_required" : total.Pending > 0 ? "pending" :
            limited ? "index_limited" : "no_pending_in_loaded_index";
        var coverage = new ReviewQueueCoverage(total,
            Count(allRows.Where(r => r.Target.Kind == "method")),
            Count(allRows.Where(r => r.Target.Kind == "type")),
            total.Pending - pendingInScope, total.Pending - visiblePending,
            total.Pending > 0, reload, status, new(snapshotId),
            reload ? "Reload the source workspace with approval, then use the NEW snapshotId with tenka_review_queue, kind=all, state=pending, no cursor or other filters. Do not mark stale work complete." :
            total.Pending > visiblePending ? "Pending targets are hidden by filters. Call tenka_review_queue with coverage.restart, clearing projectId, query and cursor; do not infer completion from this page." :
            total.Pending > 0 ? "Continue with nextCursor and offset=0. After the pass, call tenka_review_queue with coverage.restart; type and method checkpoints are independent." :
            "There are no pending checkpoints in the loaded index only. Check index limits and unsupported coverage; checked/optimized are not proof of correctness or speedup.");
        return new(snapshotId, page.Total, page.Offset, page.Items.Select(ReviewContextFingerprints.CompactForQueue).ToArray(), page.NextCursor, page.HasMore,
            "stable_project_name_target_id", Count(scoped), scoped.Any(r => r.State.RequiresReload), limited,
            ReviewProgress.Note,
            "nextCursorをcursorに渡すと、途中でチェック済みが増えても、先に返した対象の次から取得します。最後まで来たらcursorなしのpendingを再確認してください。空のページは索引外を含む確認完了を意味しません。",
            "読み込み済みの索引範囲のみです。記録不可の対象や索引上限は完了扱いにしません。状態・変更の検査は読み取り時点の情報で、複数エージェントへの排他割当てではありません。", coverage);
    }
    private static bool Pending(ReviewQueueItem row) => row.State.Status is "unreviewed" or "needs_review";
    private static ReviewQueueCounts Count(IEnumerable<ReviewQueueItem> rows)
    {
        var total = 0; var unreviewed = 0; var needsReview = 0; var checkedCount = 0;
        var optimized = 0; var untrackable = 0; var blocked = 0;
        foreach (var row in rows)
        {
            total++;
            switch (row.State.Status)
            {
                case "unreviewed": unreviewed++; break;
                case "needs_review": needsReview++; break;
                case "checked": checkedCount++; break;
                case "optimized": optimized++; break;
            }
            if (!row.Target.Trackable) untrackable++;
            if (Pending(row) && (!row.Target.Trackable || row.State.RequiresReload)) blocked++;
        }
        return new(total, unreviewed, needsReview, checkedCount, optimized, untrackable, blocked);
    }
}
