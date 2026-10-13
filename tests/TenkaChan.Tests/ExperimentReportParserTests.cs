using TenkaChan.Mcp.Experiments;
using Xunit;

namespace TenkaChan.Tests;

public sealed class ExperimentReportParserTests
{
    [Fact]
    public void ReadsPassingTrxCounters()
    {
        var path = WriteTemp(".trx", """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testName="Passes" outcome="Passed" />
              </Results>
              <ResultSummary outcome="Passed">
                <Counters total="1" executed="1" passed="1" failed="0" notExecuted="0" />
              </ResultSummary>
            </TestRun>
            """);
        try
        {
            var report = ExperimentReportParser.ReadTrx(path);
            Assert.Equal(1, report.Passed);
            Assert.Equal(0, report.Failed);
            Assert.Empty(report.Failures);
            Assert.Equal("Passed", report.Outcome);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadsStandardVstestCompletedSummaryWithPassedCounters()
    {
        var path = WriteTemp(".trx", """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <ResultSummary outcome="Completed">
                <Counters total="2" executed="2" passed="2" failed="0"
                    notExecuted="0" notRunnable="0" />
              </ResultSummary>
            </TestRun>
            """);
        try
        {
            var report = ExperimentReportParser.ReadTrx(path);
            Assert.Equal("Completed", report.Outcome);
            Assert.Equal(2, report.Passed);
            Assert.Equal(0, report.Failed);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadsFailedCasesWithoutTrustingConsoleOutput()
    {
        var path = WriteTemp(".trx", """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testName="BadCase" outcome="Failed">
                  <Output><ErrorInfo><Message>Expected 2, got 3</Message></ErrorInfo></Output>
                </UnitTestResult>
              </Results>
              <ResultSummary outcome="Failed">
                <Counters total="2" executed="2" passed="1" failed="1" notExecuted="0" />
              </ResultSummary>
            </TestRun>
            """);
        try
        {
            var report = ExperimentReportParser.ReadTrx(path);
            Assert.Equal(2, report.Total);
            Assert.Equal(1, report.Failed);
            Assert.Single(report.Failures);
            Assert.Contains("Expected 2", report.Failures[0].Message);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RejectsExternalXmlEntities()
    {
        var path = WriteTemp(".trx", """
            <!DOCTYPE foo [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <TestRun><ResultSummary outcome="Passed">
              <Counters total="1" passed="1" failed="0" />
            </ResultSummary>&xxe;</TestRun>
            """);
        try
        {
            Assert.Throws<System.Xml.XmlException>(() => ExperimentReportParser.ReadTrx(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ParsesBenchmarkJsonAsUnverifiedMeasurements()
    {
        var path = WriteTemp(".json", """
            {
              "Title": "Bench",
              "HostEnvironmentInfo": {
                "OsVersion": "Linux",
                "ProcessorName": {"Value": "Test CPU"},
                "Architecture": "x64",
                "ClrVersion": ".NET 10",
                "DotNetCliVersion": "10.0.401",
                "Configuration": "RELEASE"
              },
              "Benchmarks": [
                {
                  "Type": "Test.Bench",
                  "Method": "Run",
                  "Parameters": "N=100",
                  "Statistics": {"N": 20, "Mean": 125.4, "Median": 124.2},
                  "Memory": {"BytesAllocatedPerOperation": 64}
                }
              ]
            }
            """);
        try
        {
            var observation = Assert.Single(ExperimentReportParser.ReadBenchmarkJson(path));
            Assert.Equal("Test.Bench.Run(N=100)", observation.CaseId);
            Assert.Equal(20, observation.SampleCount);
            Assert.Equal(125.4, observation.MeanNanoseconds);
            Assert.Equal(64d, observation.AllocatedBytesPerOperation);
            Assert.Contains("Test CPU", observation.EnvironmentFingerprint);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RejectsNonBenchmarkJson()
    {
        var path = WriteTemp(".json", """{"events": []}""");
        try
        {
            Assert.Throws<InvalidDataException>(() => ExperimentReportParser.ReadBenchmarkJson(path));
        }
        finally { File.Delete(path); }
    }

    private static string WriteTemp(string extension, string text)
    {
        var path = Path.Combine(Path.GetTempPath(),
            "tenka-report-" + Guid.NewGuid().ToString("N") + extension);
        File.WriteAllText(path, text);
        return path;
    }
}
