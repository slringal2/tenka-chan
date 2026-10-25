using System.Text;
using System.Text.Json;
using TenkaChan.Mcp.Agent;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Tracking;
using Xunit;
using TrackedReviewTarget = global::TenkaChan.Mcp.Tracking.ReviewTarget;

namespace TenkaChan.Tests;

public sealed class ReviewFocusedSliceTests
{
    private static (TrackedReviewTarget Target, ReviewState State,
        ExplorerMethod Method, MethodInspection Inspection) Fixture(string source)
    {
        var method = new ExplorerMethod("method", "type", "project",
            "Fixture.Worker.Run()", "Worker.cs", 12, "Ordinary", "Worker", "Run",
            "Worker:Run", null);
        var tracked = new TrackedReviewTarget(
            new string('A', 64), "method", "method", "project", "Fixture",
            Path.GetFullPath("Fixture.csproj"), method.Signature, method.File, 12,
            ReviewProgress.Hash(source), ReviewProgress.Hash("context"),
            true, null, source);
        var state = new ReviewState("needs_review", "checked", "context_changed", 5,
            false, true, false, null)
        { ContextChange = new ReviewContextChange("changed", 2, 2,
            ["sourceFiles"], "whitespace-only edits can cause context changes") };
        var inspection = new MethodInspection(
            new MethodSummary(method.Id, "Fixture", method.Signature, method.File, 12),
            "THIS_IS_NOT_THE_FULL_TRACKED_SOURCE",
            Enumerable.Range(0, 100).Select(i => "callee_" + i).ToArray(),
            Enumerable.Range(0, 90).Select(i => "caller_" + i).ToArray(),
            Enumerable.Range(0, 101)
                .Select(i => new FlowBlock(i, "block", ["operation one", "operation two", "operation three"],
                    null, null, null)).ToArray(), [], [],
            Enumerable.Range(0, 24).Select(i => "CS000" + i).ToArray(),
            "analysis note");
        return (tracked, state, method, inspection);
    }

    [Fact]
    public void DurableTargetIdOpensSourceAndBoundedAnalysisInOneResult()
    {
        var source = new string('X', 50000);
        var (tracked, state, method, inspection) = Fixture(source);
        var result = ReviewFocusedSlices.Build("snapshot", tracked, state, method, inspection);
        Assert.Equal(tracked.Id, result.TargetId);
        Assert.Equal("method", result.MethodId);
        Assert.Equal("type", result.TypeId);
        Assert.Equal(source[..12000], result.Source);
        Assert.Equal(0, result.SourceOffset);
        Assert.Equal(12000, result.SourceLength);
        Assert.Equal(50000, result.SourceTotalChars);
        Assert.True(result.SourceHasMore);
        Assert.Equal(tracked.SourceHash, result.SourceHash);
        Assert.Equal(tracked.ContextHash, result.ContextHash);
        Assert.Equal(5, result.ExpectedRevision);
        Assert.True(result.ContextChanged);
        Assert.False(result.SourceChanged);
        Assert.Contains("sourceFiles", result.ContextChangedComponents);
        Assert.Contains("sourceOffset=12000", result.NextAction);
        Assert.Contains("Whitespace-only", result.ContextSemantics);
        Assert.Equal(100, result.Analysis!.TotalCallees);
        Assert.Equal(12, result.Analysis.Callees.Count);
        Assert.Equal(101, result.Analysis.TotalBlocks);
        Assert.Equal(12, result.Analysis.Blocks.Count);
        Assert.True(result.Analysis.BlocksTruncated);
        Assert.Equal("source_inspection_unverified", result.EvidenceStatus);
        Assert.False(result.SourceBinaryVerified);
        var json = JsonSerializer.SerializeToElement(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(JsonValueKind.String, json.GetProperty("source").ValueKind);
        Assert.False(json.TryGetProperty("data", out _));
        Assert.True(json.GetRawText().Length < 60000);
    }

    [Fact]
    public void LaterPagesUseFullTrackedSourceNotTwentyKilobyteInspectionPreview()
    {
        var source = new string('A', 43000) + "LAST_CODE";
        var (tracked, state, method, inspection) = Fixture(source);
        var last = ReviewFocusedSlices.Build("current", tracked, state, method, inspection,
            sourceOffset: 43000, sourceLimit: 12000);
        Assert.Equal("LAST_CODE", last.Source);
        Assert.False(last.SourceHasMore);
        Assert.Equal(source.Length, last.SourceTotalChars);
        Assert.Contains("review", last.NextAction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EscapedUnicodeSourceNeverOverflowsInlineJournalBudgetAndAllPagesReassemble()
    {
        // System.Text.Json escapes non-ASCII characters by default. A 12k-char
        // page can exceed 60k serialized bytes even if the source itself fits.
        var source = new string('日', 22000) + "\\r\\n" + new string('語', 10000);
        var (tracked, state, method, inspection) = Fixture(source);
        var assembled = new StringBuilder();
        var offset = 0;
        var count = 0;
        while (offset < source.Length)
        {
            var result = ReviewFocusedSlices.Build("snapshot", tracked, state, method,
                offset == 0 ? inspection : null, offset, 12000);
            Assert.True(JsonSerializer.SerializeToUtf8Bytes(result,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length
                <= ReviewFocusedSlices.MaxInlineJsonBytes);
            Assert.True(result.SourceLength > 0);
            Assert.Equal(offset, result.SourceOffset);
            Assert.Equal(offset == 0, result.AnalysisIncluded);
            assembled.Append(result.Source);
            offset += result.SourceLength;
            Assert.Equal(offset < source.Length, result.SourceHasMore);
            Assert.True(++count < 20, "Paging did not make progress.");
        }
        Assert.Equal(source, assembled.ToString());
        Assert.True(count > 3);
    }

    [Fact]
    public void SurrogatePairsAreNeverSplitAcrossSourcePageBoundaries()
    {
        var source = new string('a', 11999) + "😀" + new string('b', 6000);
        var (tracked, state, method, inspection) = Fixture(source);
        var first = ReviewFocusedSlices.Build("snapshot", tracked, state, method, inspection);
        Assert.Equal(11999, first.SourceLength);
        Assert.EndsWith("a", first.Source);
        Assert.True(first.SourceHasMore);
        var second = ReviewFocusedSlices.Build("snapshot", tracked, state, method,
            inspection: null, sourceOffset: first.SourceLength);
        Assert.StartsWith("😀", second.Source);
        Assert.Equal(source, first.Source + second.Source);
        Assert.Throws<ArgumentException>(() => ReviewFocusedSlices.Build(
            "snapshot", tracked, state, method, inspection: null, sourceOffset: 12000));
    }

    [Fact]
    public void OneCharacterPageCannotLoopForeverAtEmojiBoundary()
    {
        var (target, state, method, inspection) = Fixture("😀 next");
        var error = Assert.Throws<ArgumentException>(() =>
            ReviewFocusedSlices.Build("snapshot", target, state, method,
                inspection, sourceLimit: 1));
        Assert.Contains("surrogate pair", error.Message);
    }

    [Fact]
    public void ContinuationRequiresNoRepeatedMethodInspection()
    {
        var source = new string('x', 22000);
        var (tracked, state, method, inspection) = Fixture(source);
        var first = ReviewFocusedSlices.Build("snapshot", tracked, state, method, inspection);
        var next = ReviewFocusedSlices.Build("snapshot", tracked, state, method, null,
            sourceOffset: first.SourceLength);
        Assert.NotNull(first.Analysis);
        Assert.Null(next.Analysis);
        Assert.False(next.AnalysisIncluded);
        Assert.Equal(first.SourceHash, next.SourceHash);
        Assert.Equal(first.ExpectedRevision, next.ExpectedRevision);
    }

    [Fact]
    public void WrongMethodSelectionOrHashMustFailClosed()
    {
        var (tracked, state, method, inspection) = Fixture("source");
        Assert.Throws<InvalidOperationException>(() =>
            ReviewFocusedSlices.Build("snapshot", tracked,
                state, method with { Id = "unrelated" }, inspection));
        Assert.Throws<InvalidDataException>(() =>
            ReviewFocusedSlices.Build("snapshot", tracked with { SourceHash = ReviewProgress.Hash("different") },
                state, method, inspection));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ReviewFocusedSlices.Build("snapshot", tracked, state, method, inspection, sourceOffset: 1000));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ReviewFocusedSlices.Build("snapshot", tracked, state, method, inspection, sourceLimit: 16001));
    }

    [Fact]
    public void TypeTargetHasTrackedSourceWithoutFabricatedMethodInspection()
    {
        var source = "public class Worker { }";
        var (tracked, state, _, _) = Fixture(source);
        var type = tracked with { Kind = "type", SelectionId = "type" };
        var result = ReviewFocusedSlices.Build("current", type, state, null, null);
        Assert.Null(result.MethodId);
        Assert.Equal("type", result.TypeId);
        Assert.Equal(source, result.Source);
        Assert.Null(result.Analysis);
    }

    [Fact]
    public void IncompleteReferenceContextNeverSuggestsMarkingAsChecked()
    {
        var (tracked, state, method, inspection) = Fixture("source");
        var unavailable = state with
        {
            ContextChange = new ReviewContextChange("context_unavailable", 2, 2,
                ["metadataReferences"], "Unresolved dependency")
        };
        var result = ReviewFocusedSlices.Build("snapshot", tracked,
            unavailable, method, inspection);
        Assert.Contains("context is incomplete", result.NextAction);
        Assert.DoesNotContain("tenka_mark_review", result.NextAction);
    }

    [Fact]
    public void StaleSourceRequiresExplicitApprovedReloadBeforeMark()
    {
        var (tracked, state, method, inspection) = Fixture("source");
        var stale = state with { RequiresReload = true, Reason = "snapshot_outdated" };
        var result = ReviewFocusedSlices.Build("snapshot", tracked, stale, method, inspection);
        Assert.Contains("explicit reload_workspace approval", result.NextAction);
        Assert.True(result.RequiresReload);
        Assert.Equal("snapshot_outdated", result.Reason);
    }
}
