using TenkaChan.Mcp.Analysis;
using Xunit;

namespace TenkaChan.Tests;

public sealed class CompilerDiagnosticContextTests
{
    [Fact]
    public void RankedErrorCodesAndReferenceCandidateCountAreIndependentOfDisplayLimit()
    {
        var byId = new Dictionary<string,int>(StringComparer.Ordinal)
        {
            ["CS0246"] = 430,
            ["CS0168"] = 2,
            ["CS0012"] = 3,
            ["CS1069"] = 1
        };
        Assert.Equal(434, CompilerDiagnosticContext.PotentialReferenceResolutionErrors(byId));
        var rows = CompilerDiagnosticContext.TopErrorCodes(byId, 2);
        Assert.Equal(new[] { "CS0246", "CS0012" }, rows.Select(x => x.Id).ToArray());
        Assert.All(rows, row => Assert.True(row.MayBeReferenceResolutionRelated));
        Assert.Contains("partial .slnx", CompilerDiagnosticContext.Interpretation);
        Assert.Contains("not proven", CompilerDiagnosticContext.Interpretation);
    }

    [Fact]
    public void UnknownCompilerIdsAreNotAssumedReferenceRelated()
    {
        Assert.False(CompilerDiagnosticContext.MayBeReferenceResolutionRelated("CS0168"));
        Assert.False(CompilerDiagnosticContext.MayBeReferenceResolutionRelated("CS9000"));
        Assert.Empty(CompilerDiagnosticContext.TopErrorCodes(new Dictionary<string,int>()));
    }
}
