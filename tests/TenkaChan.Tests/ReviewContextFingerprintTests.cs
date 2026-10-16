using System.Text.Json;
using TenkaChan.Mcp.Tracking;
using Xunit;

namespace TenkaChan.Tests;

public sealed class ReviewContextFingerprintTests
{
    private static Dictionary<string, string> Components() => ReviewContextFingerprints.ComponentNames
        .ToDictionary(name => name, name => ReviewProgress.Hash(name), StringComparer.Ordinal);
    private static ReviewContextFingerprint Fingerprint(string? change = null)
    {
        var components = Components();
        if (change is not null) components[change] = ReviewProgress.Hash("changed");
        return ReviewContextFingerprints.Create(components, true);
    }
    private static ReviewTarget Target(ReviewContextFingerprint fingerprint) => new("target", "selection", "method",
        "p", "P", Path.GetFullPath("P.csproj"), "C.M()", "A.cs", 1,
        ReviewProgress.Hash("source"), fingerprint.Hash, true, null, "source")
        { ContextFingerprint = fingerprint, LegacyContextHash = ReviewProgress.Hash("legacy") };
    private static ReviewCheckpoint Mark(ReviewTarget target) => new(1, target.Id, target.Kind, target.Name,
        target.ProjectPath, "checked", target.SourceHash, target.ContextHash, "Inspected", "test", "session", "utc", [])
        { ContextFingerprint = target.ContextFingerprint };

    [Fact]
    public void OrderingAndLineEndingsAreNormalizedBeforeHashing()
    {
        Assert.Equal(ReviewContextFingerprints.Digest(["b", "a\r\n"]), ReviewContextFingerprints.Digest(["a\n", "b"]));
        Assert.NotEqual(ReviewContextFingerprints.Digest(["a"]), ReviewContextFingerprints.Digest(["a", "a"]));
    }
    [Fact]
    public void DictionaryInsertionOrderCannotChangeFingerprint()
    {
        var components = Components();
        var reversed = components.Reverse().ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        Assert.Equal(ReviewContextFingerprints.Create(components, true).Hash, ReviewContextFingerprints.Create(reversed, true).Hash);
    }
    [Theory]
    [InlineData("sourceFiles")]
    [InlineData("generatedSources")]
    [InlineData("projectFiles")]
    [InlineData("parseOptions")]
    [InlineData("compilationOptions")]
    [InlineData("metadataReferences")]
    [InlineData("projectReferences")]
    public void ChangedCategoryIsReportedWithoutPromotingTheMark(string category)
    {
        var before = Target(Fingerprint()); var after = Target(Fingerprint(category));
        var state = ReviewProgress.State(after, Mark(before), false);
        Assert.Equal("needs_review", state.Status);
        Assert.False(state.SourceChanged);
        Assert.True(state.ContextChanged);
        Assert.Equal("changed", state.ContextChange!.Status);
        Assert.Equal(category, Assert.Single(state.ContextChange.ChangedComponents));
    }
    [Fact]
    public void RemarkUsesNewContextAndDoesNotStayPermanentlyNeedsReview()
    {
        var before = Target(Fingerprint()); var after = Target(Fingerprint("sourceFiles"));
        Assert.Equal("needs_review", ReviewProgress.State(after, Mark(before), false).Status);
        var newMark = Mark(after) with { Revision = 2 };
        Assert.Equal("checked", ReviewProgress.State(after, newMark, false).Status);
        Assert.Equal("unchanged", ReviewProgress.State(after, newMark, false).ContextChange!.Status);
    }
    [Fact]
    public void ExactLegacyMatchKeepsLegacyStatusWithoutRewritingHistory()
    {
        var target = Target(Fingerprint());
        var old = Mark(target) with { ContextHash = target.LegacyContextHash!, ContextFingerprint = null };
        var state = ReviewProgress.State(target, old, false);
        Assert.Equal("checked", state.Status);
        Assert.Equal("legacy_exact_match", state.ContextChange!.Status);
        Assert.False(state.ContextChanged);
        Assert.Null(old.ContextFingerprint);
        Assert.Equal(target.LegacyContextHash, old.ContextHash);
    }
    [Fact]
    public void UnknownLegacyMismatchCannotBeRestoredFromAnUnchangedBody()
    {
        var target = Target(Fingerprint());
        var old = Mark(target) with { ContextHash = ReviewProgress.Hash("other legacy"), ContextFingerprint = null };
        var state = ReviewProgress.State(target, old, false);
        Assert.False(state.SourceChanged);
        Assert.Equal("needs_review", state.Status);
        Assert.Equal("legacy_components_unavailable", state.ContextChange!.Status);
        Assert.Empty(state.ContextChange.ChangedComponents);
    }
    [Fact]
    public void LegacyMatchingCannotHideChangedOrStaleSource()
    {
        var target = Target(Fingerprint());
        var old = Mark(target) with { ContextHash = target.LegacyContextHash!, ContextFingerprint = null };
        Assert.Equal("needs_review", ReviewProgress.State(target, old, true).Status);
        var changed = target with { Source = "different", SourceHash = ReviewProgress.Hash("different") };
        Assert.Equal("source_changed", ReviewProgress.State(changed, old, false).Reason);
    }
    [Fact]
    public void ANewFingerprintCannotFallBackToAnOldHash()
    {
        var before = Target(Fingerprint()); var after = Target(Fingerprint("metadataReferences")) with { LegacyContextHash = before.ContextHash };
        Assert.Equal("needs_review", ReviewProgress.State(after, Mark(before), false).Status);
    }
    [Fact]
    public void IncompleteContextCannotBeMarkedOrConsideredCurrent()
    {
        var fingerprint = ReviewContextFingerprints.Create(Components(), false);
        var target = Target(fingerprint);
        var state = ReviewProgress.State(target, Mark(target), false);
        Assert.Equal("needs_review", state.Status);
        Assert.Equal("context_unavailable", state.Reason);
        var request = new ReviewMarkRequest("s", target.Id, target.SourceHash, target.ContextHash, 1, "checked", "test");
        Assert.Throws<InvalidOperationException>(() => ReviewProgress.ValidateMark(target, request, false));
    }
    [Fact]
    public void FingerprintMetadataIsOptionalForOldSerializedCheckpoints()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var checkpoint = Mark(Target(Fingerprint())) with { ContextFingerprint = null };
        var element = JsonSerializer.SerializeToElement(checkpoint, options);
        var legacy = element.EnumerateObject().Where(p => p.Name != "contextFingerprint")
            .ToDictionary(p => p.Name, p => p.Value.Clone());
        var restored = JsonSerializer.Deserialize<ReviewCheckpoint>(JsonSerializer.Serialize(legacy), options)!;
        Assert.Null(restored.ContextFingerprint);
        Assert.Equal(checkpoint.ContextHash, restored.ContextHash);
    }
    [Fact]
    public void FingerprintSurvivesCheckpointSerialization()
    {
        var checkpoint = Mark(Target(Fingerprint()));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var restored = JsonSerializer.Deserialize<ReviewCheckpoint>(JsonSerializer.Serialize(checkpoint, options), options)!;
        Assert.Equal(checkpoint.ContextFingerprint!.Hash, restored.ContextFingerprint!.Hash);
        Assert.Equal(checkpoint.ContextFingerprint.Components.Select(c => c.Hash), restored.ContextFingerprint.Components.Select(c => c.Hash));
    }
    [Fact]
    public void InconsistentDiagnosticsCannotExplainAwayAHashMismatch()
    {
        var before = Target(Fingerprint()); var after = Target(Fingerprint("sourceFiles"));
        var old = Mark(before) with { ContextFingerprint = before.ContextFingerprint! with { Hash = after.ContextHash } };
        var state = ReviewProgress.State(after, old, false);
        Assert.Equal("needs_review", state.Status);
        Assert.Equal("components_inconsistent", state.ContextChange!.Status);
    }
    [Fact]
    public void ExplicitResetIsNotRevivedByMatchingContext()
    {
        var target = Target(Fingerprint()); var old = Mark(target) with { Status = "unreviewed" };
        Assert.Equal("unreviewed", ReviewProgress.State(target, old, false).Status);
    }
    [Fact]
    public void MissingComponentsAreNotSilentlyHashedAsEmpty()
    {
        var components = Components(); components.Remove("sourceFiles");
        Assert.Throws<ArgumentException>(() => ReviewContextFingerprints.Create(components, true));
    }
    [Fact]
    public void QueueResponsesDoNotRepeatManifestsOrMutateStoredRecords()
    {
        var target = Target(Fingerprint()); var checkpoint = Mark(target);
        var state = ReviewProgress.State(target, checkpoint, false);
        var view = ReviewQueueViews.Build("snapshot", [new(target, state)], state: "all");
        var item = Assert.Single(view.Items);
        Assert.Null(item.Target.ContextFingerprint);
        Assert.Null(item.State.ContextChange);
        Assert.Null(item.State.Checkpoint!.ContextFingerprint);
        Assert.NotNull(target.ContextFingerprint);
        Assert.NotNull(checkpoint.ContextFingerprint);
        Assert.NotNull(state.ContextChange);
        var json = JsonSerializer.Serialize(view, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("\"contextFingerprint\"", json);
        Assert.DoesNotContain("\"contextChange\"", json);
    }
    [Fact]
    public void StorePersistsFingerprintAcrossNewStoreInstances()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tenka-fingerprint-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "review.sqlite");
            var target = Target(Fingerprint());
            var request = new ReviewMarkRequest("snapshot", target.Id, target.SourceHash, target.ContextHash, 0, "checked", "Inspected");
            var saved = new ReviewProgressStore(path).Save(target, request, "agent", "session", []);
            var reopened = new ReviewProgressStore(path);
            var restored = reopened.Read(target.Id, saved.Revision).Checkpoint;
            Assert.Equal(target.ContextHash, restored.ContextFingerprint!.Hash);
            Assert.Equal("checked", ReviewProgress.State(target, restored, false).Status);
            Assert.Equal(target.ContextHash, reopened.Latest()[target.Id].ContextFingerprint!.Hash);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

}
