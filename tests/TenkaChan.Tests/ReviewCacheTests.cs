using Microsoft.Data.Sqlite;
using TenkaChan.Mcp.Tracking;
using Xunit;

namespace TenkaChan.Tests;

public sealed class ReviewCacheTests
{
    [Fact] public void DiffIsComputedOnceAcrossPagesAndLineEndingVariants()
    {
        var cache = new ReviewDiffCache();
        var before = string.Join("\n", Enumerable.Range(0, 100).Select(i => "old" + i));
        var after = string.Join("\n", Enumerable.Range(0, 100).Select(i => "new" + i));
        var a = cache.Compare(before, after, 0, 30);
        var b = cache.Compare(before, after, 30, 30);
        var c = cache.Compare(before.Replace("\n", "\r\n"), after.Replace("\n", "\r\n"), 60, 30);
        Assert.Equal(1, cache.Computations); Assert.Equal(30, b.Offset); Assert.Equal(60, c.Offset);
        Assert.Equal(a.Added, b.Added); Assert.Equal(a.Removed, c.Removed);
        Assert.Equal(ReviewSourceDiff.Compare(before, after, 30, 30).Rows, b.Rows);
    }
    [Fact] public void ChangedSourceCannotReuseADifferentDiff()
    {
        var cache = new ReviewDiffCache(); cache.Compare("a", "b");
        var changed = cache.Compare("a", "c");
        Assert.Equal(2, cache.Computations); Assert.Contains(changed.Rows, row => row.Text == "c");
    }
    [Fact] public void DiffCacheIsBoundedAndRecomputesEvictedEntries()
    {
        var cache = new ReviewDiffCache();
        for (var i = 0; i < 9; i++) cache.Compare("old", "new" + i);
        cache.Compare("old", "new0"); Assert.Equal(10, cache.Computations);
    }
    [Fact] public void InvalidDiffPagesDoNotPopulateCache()
    {
        var cache = new ReviewDiffCache();
        Assert.Throws<ArgumentException>(() => cache.Compare("a", "b", -1));
        Assert.Throws<ArgumentException>(() => cache.Compare("a", "b", limit: 501));
        Assert.Equal(0, cache.Computations);
    }
    [Fact] public void LatestCacheRefreshesAfterLocalAndOtherInstanceWrites()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenka-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "progress.sqlite");
            var a = new ReviewProgressStore(path); var b = new ReviewProgressStore(path);
            var old = a.Latest(); Assert.Same(old, a.Latest());
            var target = new ReviewTarget(ReviewProgress.Hash("id"), "selection", "method", "project", "Project",
                Path.Combine(root, "Project.csproj"), "Method()", "Code.cs", 1,
                ReviewProgress.Hash("code"), ReviewProgress.Hash("context"), true, null, "code");
            var request = new ReviewMarkRequest("snapshot", target.Id, target.SourceHash, target.ContextHash, 0, "checked", "Reviewed only.");
            var first = b.Save(target, request, "agent", "session", []);
            Assert.Equal(first.Revision, a.Latest()[target.Id].Revision);
            Assert.Empty(old); // Prior callers' immutable dictionary is not changed in place.
            var second = a.Save(target, request with { ExpectedRevision = first.Revision, Status = "optimized" }, "agent", "session", []);
            Assert.Equal(second.Revision, a.Latest()[target.Id].Revision);
            Assert.Equal(second.Revision, b.Latest()[target.Id].Revision);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
}
