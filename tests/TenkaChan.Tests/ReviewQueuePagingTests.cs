using System.Text.Json;
using TenkaChan.Mcp.Tracking;
using Xunit;

namespace TenkaChan.Tests;

public sealed class ReviewQueuePagingTests
{
    private static ReviewQueueItem Item(int i, string status = "unreviewed", bool trackable = true)
    {
        var target = new ReviewTarget(ReviewProgress.Hash("target-" + i), "selection-" + i, "method", "project",
            "Project", Path.Combine(Path.GetTempPath(), "Project.csproj"), "Method" + i.ToString("D5"),
            "Code.cs", i + 1, ReviewProgress.Hash("code"), ReviewProgress.Hash("context"), trackable,
            trackable ? null : "unsupported", "private source must not appear in queue JSON");
        return new(target, new(status, null, "not_checked", 0, false, false, false, null));
    }
    private static ReviewQueueSlice Page(ReviewQueueItem[] rows, string? cursor = null, string state = "pending",
        string snapshot = "snapshot", string? query = null, bool trackableOnly = false, int offset = 0, int limit = 2) =>
        ReviewQueuePager.Page(snapshot, rows, state, null, "method", query, trackableOnly, offset, limit, cursor);

    [Fact] public void MarkingPreviousPageDoesNotSkipNextUnreviewedMethods()
    {
        var rows = Enumerable.Range(0, 6).Select(i => Item(i)).ToArray();
        var first = Page(rows);
        rows[0] = rows[0] with { State = rows[0].State with { Status = "checked" } };
        rows[1] = rows[1] with { State = rows[1].State with { Status = "optimized" } };
        var next = Page(rows, first.NextCursor);
        Assert.Equal(new[] { "selection-2", "selection-3" }, next.Items.Select(r => r.Target.SelectionId));
        Assert.Equal(4, next.Total);
    }
    [Fact] public void PageOrderDoesNotDependOnStatusOrIncomingEnumerationOrder()
    {
        var rows = new[] { Item(3, "needs_review"), Item(1), Item(2, "checked"), Item(0) };
        var first = Page(rows, state: "all");
        var next = Page(rows.Reverse().ToArray(), first.NextCursor, state: "all");
        Assert.Equal(new[] { "selection-2", "selection-3" }, next.Items.Select(r => r.Target.SelectionId));
    }
    [Fact] public void SameNameDeclarationsHaveUniqueAndStableTieBreaking()
    {
        var rows = Enumerable.Range(0, 4).Select(i => Item(i)).Select(r => r with { Target = r.Target with { Name = "Same" } }).ToArray();
        var first = Page(rows);
        var last = Page(rows.Reverse().ToArray(), first.NextCursor);
        Assert.Equal(4, first.Items.Concat(last.Items).Select(r => r.Target.Id).Distinct().Count());
        Assert.False(last.HasMore); Assert.Null(last.NextCursor);
    }
    [Theory]
    [InlineData("snapshot")][InlineData("state")][InlineData("query")][InlineData("trackable")]
    public void ContinuationsCannotBeReusedForAnotherScope(string change)
    {
        var rows = new[] { Item(0), Item(1), Item(2) }; var cursor = Page(rows).NextCursor;
        Assert.Throws<ReviewConflictException>(() => Page(rows, cursor, state: change == "state" ? "all" : "pending",
            snapshot: change == "snapshot" ? "new" : "snapshot", query: change == "query" ? "Method" : null,
            trackableOnly: change == "trackable"));
    }
    [Theory] [InlineData("project")] [InlineData("kind")]
    public void ProjectAndDeclarationKindArePartOfCursorScope(string change)
    {
        var rows = new[] { Item(0), Item(1), Item(2) }; var cursor = Page(rows).NextCursor;
        Assert.Throws<ReviewConflictException>(() => ReviewQueuePager.Page("snapshot", rows, "pending",
            change == "project" ? "another-project" : null, change == "kind" ? "all" : "method", null,
            false, 0, 2, cursor));
    }
    [Theory]
    [InlineData("")][InlineData("../../../")][InlineData("%%%")] [InlineData("AA")]
    public void InvalidCursorsAreRejected(string cursor) =>
        Assert.Throws<ArgumentException>(() => Page(new[] { Item(0) }, cursor));
    [Fact] public void OversizedCursorAndMixedOffsetAreRejected()
    {
        var rows = new[] { Item(0), Item(1), Item(2) };
        Assert.Throws<ArgumentException>(() => Page(rows, new string('a', 513)));
        Assert.Throws<ArgumentException>(() => Page(rows, Page(rows).NextCursor, offset: 1));
    }
    [Fact] public void MissingAnchorIsNotSilentlyRestartedOrTreatedAsComplete()
    {
        var rows = new[] { Item(0), Item(1), Item(2) };
        var cursor = Page(rows).NextCursor;
        Assert.Throws<ReviewConflictException>(() => Page(new[] { rows[0], rows[2] }, cursor));
    }
    [Fact] public void FinishingPendingAfterAnEarlierPageReturnsAnEmptyFinalPage()
    {
        var rows = new[] { Item(0), Item(1), Item(2) }; var cursor = Page(rows).NextCursor;
        rows = rows.Select(r => r with { State = r.State with { Status = "checked" } }).ToArray();
        var page = Page(rows, cursor);
        Assert.Empty(page.Items); Assert.False(page.HasMore); Assert.Null(page.NextCursor);
    }
    [Fact] public void TrackableFilterDoesNotPretendUnsupportedTargetsWereChecked()
    {
        var rows = new[] { Item(0, trackable: false), Item(1) };
        Assert.Equal(2, Page(rows).Total);
        Assert.Single(Page(rows, trackableOnly: true).Items);
        Assert.Equal("unreviewed", rows[0].State.Status);
    }
    [Fact] public void QueueMetadataDoesNotSerializeSourceBodies()
    {
        var json = JsonSerializer.Serialize(Page(new[] { Item(0) }));
        Assert.DoesNotContain("private source must not appear", json);
    }
    [Fact] public void SequentialChecksCoverTwoThousandTargetsWithoutOffsetSkipping()
    {
        var rows = Enumerable.Range(0, 2000).Select(i => Item(i)).ToArray();
        var seen = new HashSet<string>(); string? cursor = null;
        do
        {
            var page = Page(rows, cursor, limit: 71);
            foreach (var item in page.Items)
            {
                Assert.True(seen.Add(item.Target.Id));
                var i = Array.FindIndex(rows, r => r.Target.Id == item.Target.Id);
                rows[i] = item with { State = item.State with { Status = "checked" } };
            }
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(rows.Length, seen.Count);
        Assert.Empty(Page(rows).Items);
    }
    [Fact] public void PreviouslyPassedTargetResetNeedsANewPassNotFalseCompletion()
    {
        var rows = new[] { Item(0), Item(1), Item(2) };
        var first = Page(rows);
        rows[0] = rows[0] with { State = rows[0].State with { Status = "checked" } };
        var second = Page(rows, first.NextCursor);
        Assert.Single(second.Items);
        // A continuation is a traversal, not a proof/claim that all earlier work is complete.
        Assert.Contains(Page(rows).Items, r => r.Target.Id == rows[1].Target.Id);
    }
}
