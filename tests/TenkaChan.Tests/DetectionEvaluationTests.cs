using TenkaChan.Mcp.Analysis;
using Xunit;
namespace TenkaChan.Tests;
public sealed class DetectionEvaluationTests
{
    [Fact]
    public void CountsErrorsAndUsesUniqueActuallyReturnedTopK()
    {
        var report = DetectionEvaluation.Evaluate([new("a", true), new("b", true), new("c", false)], ["c", "a", "a"], 10);
        Assert.Equal(1, report.TruePositives); Assert.Equal(1, report.FalsePositives); Assert.Equal(1, report.FalseNegatives);
        Assert.Equal(0.5, report.Precision); Assert.Equal(0.5, report.Recall); Assert.Equal(0.5, report.PrecisionAtK);
        Assert.Equal(2, report.ReturnedTopK); Assert.Equal(10, report.RequestedTopK);
        Assert.Contains("b", report.MissedIds); Assert.Contains("c", report.FalsePositiveIds);
    }
    [Fact]
    public void ZeroDenominatorsAreUnknownNotPerfectScores()
    {
        var report = DetectionEvaluation.Evaluate([], []);
        Assert.Null(report.Precision); Assert.Null(report.Recall); Assert.Null(report.PrecisionAtK);
    }
    [Fact]
    public void UnlabeledPredictionsAndDuplicateTruthAreRejected()
    {
        Assert.Throws<ArgumentException>(() => DetectionEvaluation.Evaluate([new("known", true)], ["unknown"]));
        Assert.Throws<ArgumentException>(() => DetectionEvaluation.Evaluate([new("same", true), new("same", false)], []));
    }
}
