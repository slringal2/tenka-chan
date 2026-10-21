using Microsoft.Build.Locator;
using Microsoft.Data.Sqlite;
using TenkaChan.Mcp.Agent;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Security;
using TenkaChan.Mcp.Tracking;
using TenkaChan.Web;
using Xunit;

namespace TenkaChan.Tests;

/// <summary>
/// Runs against an actual, trusted MSBuildWorkspace fixture when .NET SDK is
/// available. Does not execute the fixture or enable generated-host experiments.
/// </summary>
public sealed class ReviewFocusIntegrationTests
{
    [Fact]
    public async Task DurableTargetFocusPagesWholeSourceAndReloadRejectsStaleSnapshot()
    {
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
        var root = Path.Combine(Path.GetTempPath(),
            "tenka-focus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "Focus.csproj");
            File.WriteAllText(path, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            var lines = Enumerable.Range(0, 600)
                .Select(i => "sum += " + i + "; // deliberate padding for paged source coverage");
            File.WriteAllText(Path.Combine(root, "Worker.cs"),
                "public static class Worker { public static int Run() { int sum=0;\n" +
                string.Join("\n", lines) + "\n return sum; } }");
            var guard = new WorkspaceGuard([root], true, false);
            using var roslyn = new RoslynReviewService(guard);
            var dashboard = new DashboardState(roslyn);
            var open = await dashboard.AgentOpenAsync(path, null, default);
            var index = await roslyn.ExplorerAsync(default);
            var catalog = await index.ReviewCatalogAsync(guard, default);
            var target = Assert.Single(catalog.Targets, t => t.Kind == "method" &&
                t.Name.Contains("Worker.Run", StringComparison.Ordinal));
            var store = new ReviewProgressStore(Path.Combine(root, "review.sqlite"));
            var first = Assert.IsType<ReviewFocusedSlice>(await ReviewFocusReader.ReadAsync(
                dashboard, guard, store, open.SnapshotId, target.Id, 0, 12000, default));
            Assert.True(first.SourceHasMore);
            Assert.Equal(target.Id, first.TargetId);
            Assert.Equal(target.SourceHash, first.SourceHash);
            Assert.Equal(target.ContextHash, first.ContextHash);
            Assert.NotNull(first.Analysis);
            Assert.Equal(0, first.ExpectedRevision);
            var remaining = Assert.IsType<ReviewFocusedSlice>(await ReviewFocusReader.ReadAsync(
                dashboard, guard, store, open.SnapshotId, target.Id, 12000, 16000, default));
            Assert.Equal(first.SourceTotalChars, remaining.SourceTotalChars);
            Assert.Null(remaining.Analysis); // Continuation must skip CFG re-computation.
            Assert.False(remaining.AnalysisIncluded);
            Assert.Equal(target.Source[..(12000 + remaining.SourceLength)],
                first.Source + remaining.Source);

            var reloaded = await dashboard.AgentReloadAsync(open.SnapshotId, default);
            Assert.NotEqual(open.SnapshotId, reloaded.SnapshotId);
            await Assert.ThrowsAsync<WorkspaceBuildConflictException>(() =>
                dashboard.AgentReloadAsync(open.SnapshotId, default));
            // Revision-free durable targetId still resolves after the new snapshot,
            // but old ephemeral IDs are never substituted for it.
            var latest = Assert.IsType<ReviewFocusedSlice>(await ReviewFocusReader.ReadAsync(
                dashboard, guard, store, reloaded.SnapshotId, target.Id, 0, 100, default));
            Assert.Equal(target.Id, latest.TargetId);
            Assert.Equal(reloaded.SnapshotId, latest.SnapshotId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
