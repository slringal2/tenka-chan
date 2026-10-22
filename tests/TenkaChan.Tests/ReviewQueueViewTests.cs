using System.Text.Json;
using TenkaChan.Mcp.Agent;
using TenkaChan.Mcp.Tracking;
using Xunit;

namespace TenkaChan.Tests;

public sealed class ReviewQueueViewTests
{
    private static ReviewQueueItem Row(string name, string kind = "method", string status = "unreviewed",
        string project = "p", bool trackable = true, bool reload = false) =>
        new(new(ReviewProgress.Hash(kind + name + project), "selection-" + name, kind, project, project,
            Path.GetFullPath(project + ".csproj"), name, "Source.cs", 1, ReviewProgress.Hash(name),
            ReviewProgress.Hash("context"), trackable, trackable ? null : "unsupported", name),
            new(status, null, "test", 0, false, false, reload, null));
    private static ReviewQueueItem[] SevenPendingTypes() =>
        [Row("Done", status: "checked"), ..Enumerable.Range(1, 7).Select(i => Row("Type" + i, "type"))];

    public static IEnumerable<object[]> ValidFilterPairs()
    {
        foreach (var kind in new[] { "all", "type", "method" })
        foreach (var state in new[] { "all", "pending", "unreviewed", "needs_review", "checked", "optimized" })
            yield return [kind, state];
    }

    [Theory]
    [MemberData(nameof(ValidFilterPairs))]
    public void GatewayValidatorAcceptsEverySupportedPairAndReturns(string kind, string state)
    {
        // Call the gateway's entry point directly. Testing Build alone did not
        // cover the accidental self-recursion formerly in ValidateFilters.
        ReviewQueueViews.ValidateFilters(kind, state);
        var result = ReviewQueueViews.Build("snapshot", [], kind: kind, state: state);
        Assert.Empty(result.Items);
    }

    [Theory]
    [InlineData("pending", "pending", "kind")]
    [InlineData("all", "method", "state")]
    [InlineData("", "pending", "kind")]
    [InlineData("ALL", "pending", "kind")]
    [InlineData("method", "", "state")]
    public void GatewayAndBuilderRejectTheSameInvalidFilters(string kind, string state, string parameter)
    {
        var direct = Assert.Throws<ArgumentException>(() => ReviewQueueViews.ValidateFilters(kind, state));
        var built = Assert.Throws<ArgumentException>(() =>
            ReviewQueueViews.Build("snapshot", [], kind: kind, state: state));
        Assert.Equal(parameter, direct.ParamName);
        Assert.Equal(direct.Message, built.Message);
    }

    [Fact]
    public void DefaultIncludesSevenTypesAfterAllMethodsWereChecked()
    {
        var result = ReviewQueueViews.Build("snapshot", SevenPendingTypes());
        Assert.Equal(7, result.Total);
        Assert.All(result.Items, row => Assert.Equal("type", row.Target.Kind));
        Assert.Equal(8, result.Summary.Total);
        Assert.Equal(7, result.Coverage.Types.Pending);
        Assert.Equal(0, result.Coverage.Methods.Pending);
        Assert.Equal(0, result.Coverage.PendingHiddenByFilters);
        Assert.True(result.Coverage.HasPendingInLoadedIndex);
    }
    [Fact]
    public void McpSchemaDefaultIsAlsoAll()
    {
        var parameter = typeof(ReviewProgressTools).GetMethod(nameof(ReviewProgressTools.ReviewQueue))!
            .GetParameters().Single(p => p.Name == "kind");
        Assert.Equal("all", parameter.DefaultValue);
        Assert.Equal(ReviewQueueViews.DefaultKind, parameter.DefaultValue);
    }
    [Fact]
    public void EmptyMethodPageStillReportsSevenHiddenTypes()
    {
        var result = ReviewQueueViews.Build("snapshot", SevenPendingTypes(), kind: "method");
        Assert.Empty(result.Items);
        Assert.False(result.HasMore);
        Assert.Equal(0, result.Total);
        Assert.Equal(1, result.Summary.Total); // Backward-compatible scoped summary.
        Assert.Equal(7, result.Coverage.PendingOutsideScope);
        Assert.Equal(7, result.Coverage.PendingHiddenByFilters);
        Assert.Equal("pending", result.Coverage.Status);
        Assert.Equal("all", result.Coverage.Restart.Kind);
        Assert.Null(result.Coverage.Restart.Cursor);
        Assert.Null(result.Coverage.Restart.ProjectId);
        Assert.Null(result.Coverage.Restart.Query);
        Assert.False(result.Coverage.Restart.TrackableOnly);
    }
    [Fact]
    public void ProjectAndQueryFiltersCannotHideGlobalPendingCount()
    {
        var result = ReviewQueueViews.Build("snapshot", [Row("a"), Row("b", project: "other")],
            project: "p", query: "does-not-exist");
        Assert.Empty(result.Items);
        Assert.Equal(2, result.Coverage.LoadedIndex.Pending);
        Assert.Equal(2, result.Coverage.PendingOutsideScope);
        Assert.Equal(2, result.Coverage.PendingHiddenByFilters);
    }
    [Fact]
    public void CheckedStateFilterStillShowsPendingCoverage()
    {
        var result = ReviewQueueViews.Build("snapshot", SevenPendingTypes(), state: "checked");
        Assert.Single(result.Items);
        Assert.Equal(0, result.Coverage.PendingOutsideScope);
        Assert.Equal(7, result.Coverage.PendingHiddenByFilters);
    }
    [Fact]
    public void TrackableFilterDoesNotCompleteUnsupportedTargets()
    {
        var result = ReviewQueueViews.Build("snapshot", [Row("a", trackable: false)], trackableOnly: true);
        Assert.Empty(result.Items);
        Assert.Equal(1, result.Coverage.LoadedIndex.BlockedPending);
        Assert.Equal(1, result.Coverage.PendingHiddenByFilters);
        Assert.True(result.Coverage.HasPendingInLoadedIndex);
    }
    [Fact]
    public void HiddenReloadRequirementIsReportedGlobally()
    {
        var result = ReviewQueueViews.Build("snapshot", [Row("type", "type", reload: true)], kind: "method");
        Assert.False(result.RequiresReload); // Existing scoped field.
        Assert.True(result.Coverage.RequiresReloadInLoadedIndex);
        Assert.Equal("reload_required", result.Coverage.Status);
        Assert.Equal(1, result.Coverage.LoadedIndex.BlockedPending);
    }
    [Fact]
    public void PagingDoesNotCountLaterPagesAsHiddenByFilters()
    {
        var result = ReviewQueueViews.Build("snapshot", [Row("a"), Row("b")], limit: 1);
        Assert.True(result.HasMore);
        Assert.Equal(2, result.Coverage.LoadedIndex.Pending);
        Assert.Equal(0, result.Coverage.PendingHiddenByFilters);
    }
    [Fact]
    public void CursorContinuesAcrossIndependentTypeAndMethodMarks()
    {
        var rows = new[] { Row("a"), Row("b", "type"), Row("c") };
        var first = ReviewQueueViews.Build("snapshot", rows, limit: 1);
        Assert.NotNull(first.NextCursor);
        rows[0] = rows[0] with { State = rows[0].State with { Status = "checked" } };
        var next = ReviewQueueViews.Build("snapshot", rows, limit: 1, cursor: first.NextCursor);
        Assert.Equal("b", Assert.Single(next.Items).Target.Name);
        Assert.Equal(2, next.Coverage.LoadedIndex.Pending);
    }
    [Fact]
    public void OldMethodCursorMustNotSilentlyBecomeAnAllCursor()
    {
        var rows = new[] { Row("a"), Row("b"), Row("c", "type") };
        var first = ReviewQueueViews.Build("snapshot", rows, kind: "method", limit: 1);
        Assert.Throws<ReviewConflictException>(() => ReviewQueueViews.Build("snapshot", rows, cursor: first.NextCursor));
    }
    [Fact]
    public void EmptyLimitedIndexIsNotComplete()
    {
        var result = ReviewQueueViews.Build("snapshot", [], limited: true);
        Assert.Equal("index_limited", result.Coverage.Status);
        Assert.True(result.Limited);
        Assert.StartsWith(ReviewProgress.Note, result.Note);
        Assert.Contains("tenka_review_focus", result.Note);
    }
    [Fact]
    public void KindPendingMistakeExplainsThatPendingBelongsToState()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            ReviewQueueViews.Build("snapshot", [Row("m")], kind: "pending"));
        Assert.Contains("kind=all|type|method", error.Message);
        Assert.Contains("state=pending", error.Message);
        var invalidState = Assert.Throws<ArgumentException>(() =>
            ReviewQueueViews.Build("snapshot", [Row("m")], state: "method"));
        Assert.Contains("kind is separately all|type|method", invalidState.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public void PageBoundsAreStillEnforced(int limit) =>
        Assert.Throws<ArgumentException>(() => ReviewQueueViews.Build("snapshot", [], limit: limit));
    [Fact]
    public void SerializedResponsePreservesOldFieldsAndAddsCoverage()
    {
        var result = JsonSerializer.SerializeToElement(ReviewQueueViews.Build("snapshot", SevenPendingTypes()),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(1, result.GetProperty("summary").GetProperty("checkedCount").GetInt32());
        Assert.Equal(7, result.GetProperty("coverage").GetProperty("types").GetProperty("pending").GetInt32());
        Assert.Equal("snapshot", result.GetProperty("coverage").GetProperty("restart").GetProperty("snapshotId").GetString());
        Assert.StartsWith(ReviewProgress.Note, result.GetProperty("note").GetString());
        Assert.Contains("tenka_review_focus", result.GetProperty("note").GetString());
    }
}
