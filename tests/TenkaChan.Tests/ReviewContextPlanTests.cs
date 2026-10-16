using TenkaChan.Mcp.Agent;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Tracking;
using Xunit;
// A same-named alias outside this namespace is hidden by Analysis.ReviewTarget.
using TrackedReviewTarget = global::TenkaChan.Mcp.Tracking.ReviewTarget;

namespace TenkaChan.Tests;

public sealed class ReviewContextPlanTests
{
    private const string Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly string Dll = Path.GetFullPath("context-target.dll");
    private static ExplorerMethod Method() => new("m", "t", "p", "C.Run(int)", "C.cs", 1, "Ordinary", "C", "Run", "C:Run", null);
    private static TrackedReviewTarget Target(string kind = "method") => new("target", kind == "method" ? "m" : "t", kind,
        "p", "Project", "Project.csproj", "C.Run", "C.cs", 1, Hash, Hash, true, null, "source");
    private static ExplorerAssembly Assembly() => new("assembly", "p", "Project", Dll, "net8.0", "Release", false, "library");
    private static BinaryMethodCatalog Catalog(params BinaryMethodEntry[] methods) => new(Dll, Hash, "mvid", methods.Length, false, methods);

    [Fact]
    public void MethodSelectionCarriesAllSourceIds()
    {
        var selection = ReviewContextPlan.Selection("snapshot", Target(), Method());
        Assert.Equal(new ReviewSelection("snapshot", "target", "method", "p", "t", "m"), selection);
    }
    [Fact]
    public void TypeTargetUsesItsOwnSelectionWithoutInventingAMethod()
    {
        var selection = ReviewContextPlan.Selection("snapshot", Target("type"), null);
        Assert.Equal("t", selection.TypeId);
        Assert.Null(selection.MethodId);
        Assert.Equal("type", selection.Kind);
    }
    [Fact]
    public void UnrelatedMethodOrProjectIsRejected()
    {
        Assert.Throws<ArgumentException>(() => ReviewContextPlan.Selection("snapshot", Target(), Method() with { Id = "other" }));
        Assert.Throws<ArgumentException>(() => ReviewContextPlan.Selection("snapshot", Target(), Method() with { ProjectId = "other" }));
        Assert.Throws<ArgumentException>(() => ReviewContextPlan.Selection("snapshot", Target("type"), Method()));
    }
    [Fact]
    public void OverloadsRemainSeparateAndCarryCompleteIlArguments()
    {
        var method = Method();
        var result = ReviewContextPlan.Binary(ReviewContextPlan.Selection("snapshot", Target(), method), method, Assembly(),
            Catalog(new(0x06000001, "C", "Run", "C::Run(int)", true), new(0x06000002, "C", "Run", "C::Run(string)", true)));
        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal(new ReviewIlArguments("snapshot", "m", "assembly", 0x06000001, Hash), result.Candidates[0].Arguments);
        Assert.Equal(0x06000002, result.Candidates[1].Arguments.MetadataToken);
        Assert.All(result.Candidates, c => Assert.Equal("tenka_il", c.Tool));
        Assert.Equal("net8.0", result.TargetFramework);
        Assert.Equal("Release", result.Configuration);
    }
    [Fact]
    public void SimilarNamesAndOtherTypesAreNotCandidateLinks()
    {
        var method = Method();
        var result = ReviewContextPlan.Binary(ReviewContextPlan.Selection("snapshot", Target(), method), method, Assembly(),
            Catalog(new(0x06000001, "Other", "Run", "Other::Run()", true), new(0x06000002, "C", "RunMore", "C::RunMore()", true)));
        Assert.Empty(result.Candidates);
    }
    [Fact]
    public void CrossProjectAndCrossAssemblyCatalogsAreRejected()
    {
        var method = Method(); var selection = ReviewContextPlan.Selection("snapshot", Target(), method);
        Assert.Throws<ArgumentException>(() => ReviewContextPlan.Binary(selection, method, Assembly() with { ProjectId = "other" }, Catalog()));
        Assert.Throws<ArgumentException>(() => ReviewContextPlan.Binary(selection, method, Assembly(),
            Catalog() with { AssemblyPath = Path.GetFullPath("different.dll") }));
        Assert.Throws<ArgumentException>(() => ReviewContextPlan.Binary(selection with { MethodId = "other" }, method, Assembly(), Catalog()));
    }
    [Theory]
    [InlineData("")]
    [InlineData("old-hash")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    public void MissingOrInvalidHashCannotBecomeAnIlCall(string hash)
    {
        var method = Method();
        Assert.Throws<ArgumentException>(() => ReviewContextPlan.Binary(ReviewContextPlan.Selection("snapshot", Target(), method),
            method, Assembly(), Catalog() with { AssemblySha256 = hash }));
    }
    [Fact]
    public void CandidateBudgetIsVisibleRatherThanAnImplicitSelection()
    {
        var method = Method();
        var rows = Enumerable.Range(1, 51).Select(i => new BinaryMethodEntry(0x06000000 + i, "C", "Run", "overload" + i, true)).ToArray();
        var result = ReviewContextPlan.Binary(ReviewContextPlan.Selection("snapshot", Target(), method), method, Assembly(), Catalog(rows));
        Assert.Equal(ReviewContextPlan.MaximumCandidatesPerAssembly, result.Candidates.Count);
        Assert.True(result.Limited);
    }
    [Fact]
    public void TruncatedCatalogRemainsLimitedEvenWithoutExactMatches()
    {
        var method = Method();
        var result = ReviewContextPlan.Binary(ReviewContextPlan.Selection("snapshot", Target(), method), method, Assembly(), Catalog() with { Truncated = true });
        Assert.Empty(result.Candidates);
        Assert.True(result.Limited);
    }
}
