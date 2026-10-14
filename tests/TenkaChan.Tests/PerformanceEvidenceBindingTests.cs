using TenkaChan.Mcp.Experiments;
using Xunit;

namespace TenkaChan.Tests;

public sealed class PerformanceEvidenceBindingTests
{
    [Theory]
    [InlineData("int Calc::Sum(int[])", "Calc:Sum(int[]):int")]
    [InlineData("void App.Work::Render()", "App.Work:Render():void")]
    [InlineData("string App.Work::Describe(System.Int32, System.String)",
        "App.Work:Describe(int,string):string")]
    public void MatchesSameCapturedMethodParameters(
        string metadata, string jit)
    {
        Assert.True(PerformanceEvidenceService.MethodParametersAgree(metadata, jit));
    }

    [Theory]
    [InlineData("int Calc::Sum(int[])", "Calc:Sum(long[]):int")]
    [InlineData("int Calc::Sum(int)", "Calc:Sum(int,int):int")]
    [InlineData("int Calc::Sum(int)", "Calc:Sum")]
    [InlineData("int Calc::Sum(int[])", "Calc:Sum(int[]):int, extra")]
    public void RefusesDifferentOrMissingParameters(
        string metadata, string jit)
    {
        Assert.False(PerformanceEvidenceService.MethodParametersAgree(metadata, jit));
    }
}
