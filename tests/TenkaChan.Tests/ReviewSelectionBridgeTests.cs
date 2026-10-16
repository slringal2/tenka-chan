using System.Text.Json;
using TenkaChan.Mcp.Tracking;
using Xunit;

namespace TenkaChan.Tests;

public sealed class ReviewSelectionBridgeTests
{
    private static ReviewTarget Target(string id = "persistent", string selection = "method", string kind = "method") =>
        new(id, selection, kind, "project", "P", "P.csproj", "M", "M.cs", 1,
            ReviewProgress.Hash("source"), "context", true, null, "source");
    private static ReviewSourceCatalog Catalog(params ReviewTarget[] targets) =>
        new("snapshot", targets, [], new Dictionary<string, HashSet<string>>(), false);
    private static ReviewCheckpoint Checkpoint(string source, string context) =>
        new(7, "persistent", "method", "M", "P.csproj", "checked", source, context, "reviewed", "agent", "session", "2026-10-16", []);

    [Theory]
    [InlineData("persistent")]
    [InlineData("method")]
    public void ExactCurrentIdsResolveToTheSamePersistentTarget(string id)
    {
        var target = Target(); Assert.Same(target, Catalog(target).ResolveForRead(id));
    }
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingIdsAreRejected(string id) =>
        Assert.Throws<ArgumentException>(() => Catalog(Target()).ResolveForRead(id));
    [Fact] public void OldSelectionOrDisplayNameIsNeverGuessed()
    {
        var catalog = Catalog(Target(selection: "new-method"));
        Assert.Throws<KeyNotFoundException>(() => catalog.ResolveForRead("old-method"));
        Assert.Throws<KeyNotFoundException>(() => catalog.ResolveForRead("M"));
        Assert.Equal("persistent", catalog.ResolveForRead("persistent").Id);
    }
    [Fact] public void AmbiguousIdsAcrossNamespacesAreRejected() =>
        Assert.Throws<InvalidOperationException>(() => Catalog(Target(), Target("other", "persistent")).ResolveForRead("persistent"));
    [Fact] public void WritesStillRequirePersistentIdsAndExactHashes()
    {
        var target = Target(); var catalog = Catalog(target);
        Assert.Throws<KeyNotFoundException>(() => catalog.Require(target.SelectionId));
        var wrong = new ReviewMarkRequest("snapshot", target.SelectionId, target.SourceHash, target.ContextHash, 0, "checked", "reviewed");
        Assert.Throws<ReviewConflictException>(() => ReviewProgress.ValidateMark(target, wrong, false));
        ReviewProgress.ValidateMark(target, wrong with { TargetId = target.Id }, false);
        Assert.Throws<ReviewConflictException>(() => ReviewProgress.ValidateMark(target, wrong with { TargetId = target.Id, ContextHash = "old" }, false));
    }
    [Theory]
    [InlineData("method", "methodId", "typeId")]
    [InlineData("type", "typeId", "methodId")]
    public void AliasSerializationKeepsOldFieldsWithoutLeakingSource(string kind, string populated, string empty)
    {
        var target = Target(kind: kind);
        var json = JsonSerializer.SerializeToElement(target, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(target.Id, json.GetProperty("id").GetString());
        Assert.Equal(target.Id, json.GetProperty("targetId").GetString());
        Assert.Equal(target.SelectionId, json.GetProperty(populated).GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty(empty).ValueKind);
        Assert.False(json.TryGetProperty("source", out _));
    }
    [Fact] public void NavigationHasPinnedReadOnlyActionsAndNoFabricatedReview()
    {
        var target = Target(); var state = ReviewProgress.State(target, Checkpoint(target.SourceHash, target.ContextHash), false);
        var nav = ReviewNavigation.ForTarget("snapshot", target, state);
        Assert.Equal(new[] { "tenka_inspect_method", "tenka_method_references", "tenka_review_diff" }, nav.NextActions.Select(a => a.Tool));
        Assert.All(nav.NextActions, a => Assert.Equal("snapshot", a.Arguments.GetProperty("snapshotId").GetString()));
        var inputs = nav.ReviewInputs!.Value;
        Assert.Equal("persistent", inputs.GetProperty("targetId").GetString());
        Assert.Equal(7, inputs.GetProperty("expectedRevision").GetInt64());
        Assert.False(inputs.TryGetProperty("status", out _));
        Assert.False(inputs.TryGetProperty("approved", out _));
        Assert.False(inputs.TryGetProperty("evidenceCallIds", out _));
    }
    [Fact] public void StaleSourceDoesNotAdvertiseActionableMarkInputs()
    {
        var target = Target(); var nav = ReviewNavigation.ForTarget("snapshot", target, ReviewProgress.State(target, null, true));
        Assert.Empty(nav.NextActions); Assert.Null(nav.ReviewInputs);
    }
    [Fact] public void UntrackableTargetsDoNotAdvertiseDiffOrMarkInputs()
    {
        var target = Target() with { Trackable = false };
        var nav = ReviewNavigation.ForTarget("snapshot", target, ReviewProgress.State(target, null, false));
        Assert.Null(nav.ReviewInputs); Assert.DoesNotContain(nav.NextActions, a => a.Tool == "tenka_review_diff");
    }
    [Fact] public void UnchangedSourceCanHaveDifferentContextWithoutSuppressingRecheck()
    {
        var target = Target(); var before = Checkpoint(target.SourceHash, "old-context");
        var comparison = ReviewNavigation.CompareHashes(before, null, target);
        Assert.False(comparison.SourceChanged); Assert.True(comparison.ContextChanged);
        Assert.False(comparison.ContextInputsAvailable);
        Assert.Equal("old-context", comparison.FromContextHash); Assert.Equal("context", comparison.ToContextHash);
        Assert.Equal("needs_review", ReviewProgress.State(target, before, false).Status);
    }
    [Fact] public void SavedToSavedComparisonUsesSavedContextNotCurrentContext()
    {
        var a = Checkpoint("old-source", "saved-context"); var b = a with { Revision = 8 };
        var comparison = ReviewNavigation.CompareHashes(a, b, Target());
        Assert.False(comparison.SourceChanged); Assert.False(comparison.ContextChanged);
        Assert.Equal("saved-context", comparison.ToContextHash);
    }
    [Fact] public void MissingBaselineIsUnknownNotUnchanged()
    {
        var comparison = ReviewNavigation.CompareHashes(null, null, Target());
        Assert.False(comparison.HasBaseline); Assert.Null(comparison.SourceChanged); Assert.Null(comparison.ContextChanged);
    }
}
