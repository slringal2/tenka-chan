using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace TenkaChan.Mcp.Experiments;

public sealed record TestCaseObservation(string Name, string Outcome, string? Message);
public sealed record TestRunSummary(int Total, int Passed, int Failed, int Skipped,
    string Outcome, IReadOnlyList<TestCaseObservation> Failures);

public sealed record BenchmarkObservation(string CaseId, string Type, string Method,
    string Parameters, int SampleCount, double? MeanNanoseconds,
    double? MedianNanoseconds, double? AllocatedBytesPerOperation,
    string EnvironmentFingerprint = "");

/// <summary>
/// Parse trusted-format result files written by an untrusted sandbox.
/// Counts come from TRX; console output is never used to infer test success.
/// </summary>
public static class ExperimentReportParser
{
    public static TestRunSummary ReadTrx(string file)
    {
        if (new FileInfo(file).Length > 8_000_000)
            throw new InvalidDataException("TRX report exceeds 8 MB.");

        using var reader = XmlReader.Create(file, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 8_000_000
        });
        var doc = XDocument.Load(reader, LoadOptions.None);
        if (doc.Root?.Name.LocalName != "TestRun")
            throw new InvalidDataException("TRX root must be TestRun.");

        var summary = doc.Descendants().FirstOrDefault(x => x.Name.LocalName == "ResultSummary")
            ?? throw new InvalidDataException("TRX missing ResultSummary.");
        var counters = summary.Descendants().FirstOrDefault(x => x.Name.LocalName == "Counters")
            ?? throw new InvalidDataException("TRX missing Counters.");
        var total = ParseCount(counters, "total");
        var passed = ParseCount(counters, "passed");
        var failed = ParseCount(counters, "failed");
        var skipped = ParseOptionalCount(counters, "notExecuted") +
            ParseOptionalCount(counters, "notRunnable");
        if (total < passed + failed + skipped)
            throw new InvalidDataException("TRX counters are internally inconsistent.");

        var failureList = doc.Descendants()
            .Where(x => x.Name.LocalName == "UnitTestResult"
                && string.Equals((string?)x.Attribute("outcome"), "Failed",
                    StringComparison.OrdinalIgnoreCase))
            .Take(50)
            .Select(result =>
            {
                var raw = (string?)result.Attribute("testName") ?? "unnamed";
                var name = raw.Length > 300 ? raw[..300] : raw;
                var message = result.Descendants()
                    .FirstOrDefault(x => x.Name.LocalName == "Message")?.Value;
                if (message?.Length > 1200) message = message[..1200];
                return new TestCaseObservation(name, "Failed", message);
            }).ToArray();

        var outcome = (string?)summary.Attribute("outcome") ?? "Unknown";
        return new TestRunSummary(total, passed, failed, skipped, outcome, failureList);
    }

    public static IReadOnlyList<BenchmarkObservation> ReadBenchmarkJson(string file)
    {
        if (new FileInfo(file).Length > 10_000_000)
            throw new InvalidDataException("Benchmark JSON report exceeds 10 MB.");

        using var stream = File.OpenRead(file);
        using var json = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            MaxDepth = 40,
            CommentHandling = JsonCommentHandling.Disallow
        });
        if (json.RootElement.ValueKind != JsonValueKind.Object ||
            !json.RootElement.TryGetProperty("Benchmarks", out var benchmarks) ||
            benchmarks.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Not a BenchmarkDotNet JSON report.");

        var fingerprint = ReadHostFingerprint(json.RootElement);
        var results = new List<BenchmarkObservation>();
        foreach (var item in benchmarks.EnumerateArray().Take(500))
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var type = ReadString(item, "Type");
            var method = ReadString(item, "Method");
            if (string.IsNullOrWhiteSpace(method)) continue;
            var parameters = ReadString(item, "Parameters");
            var samples = 0;
            double? mean = null, median = null, allocated = null;

            if (item.TryGetProperty("Statistics", out var stats)
                && stats.ValueKind == JsonValueKind.Object)
            {
                samples = (int)Math.Clamp(ReadNumber(stats, "N") ?? 0d, 0d, 1_000_000d);
                mean = ReadNumber(stats, "Mean");
                median = ReadNumber(stats, "Median");
            }

            if (item.TryGetProperty("Memory", out var memory)
                && memory.ValueKind == JsonValueKind.Object)
                allocated = ReadNumber(memory, "BytesAllocatedPerOperation");

            // Statistics are observational. Do not infer confidence intervals from
            // means alone; PerformanceStatistics requires independent raw samples.
            var caseId = $"{type}.{method}({parameters})";
            results.Add(new BenchmarkObservation(caseId, type, method, parameters,
                samples, mean, median, allocated, fingerprint));
        }
        return results;
    }


    private static string ReadHostFingerprint(JsonElement root)
    {
        if (!root.TryGetProperty("HostEnvironmentInfo", out var environment) ||
            environment.ValueKind != JsonValueKind.Object)
            return "";

        var cpu = ReadEnvironmentString(environment, "ProcessorName");
        var os = ReadEnvironmentString(environment, "OsVersion");
        var arch = ReadEnvironmentString(environment, "Architecture");
        var runtime = ReadEnvironmentString(environment, "RuntimeVersion");
        if (string.IsNullOrEmpty(runtime)) runtime = ReadEnvironmentString(environment, "ClrVersion");
        if (string.IsNullOrEmpty(cpu) || string.IsNullOrEmpty(os) ||
            string.IsNullOrEmpty(arch) || string.IsNullOrEmpty(runtime))
            return "";

        var sdk = ReadEnvironmentString(environment, "DotNetCliVersion");
        var config = ReadEnvironmentString(environment, "Configuration");
        return string.Join("|", os, cpu, arch, runtime, sdk, config);
    }

    private static string ReadEnvironmentString(JsonElement environment, string key)
    {
        if (!environment.TryGetProperty(key, out var element))
            return "";
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("Value", out var nested))
            element = nested;
        return element.ValueKind == JsonValueKind.String
            ? (element.GetString() ?? "").Trim()
            : "";
    }

    private static int ParseCount(XElement element, string name)
    {
        var value = (string?)element.Attribute(name);
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || count < 0 || count > 100_000_000)
            throw new InvalidDataException($"Invalid TRX counter: {name}.");
        return count;
    }

    private static int ParseOptionalCount(XElement element, string name)
    {
        var value = (string?)element.Attribute(name);
        return value is null ? 0 : ParseCount(element, name);
    }

    private static string ReadString(JsonElement value, string name)
    {
        return value.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String
            ? (property.GetString() ?? "")[..Math.Min(property.GetString()!.Length, 300)]
            : "";
    }

    private static double? ReadNumber(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.Number ||
            !property.TryGetDouble(out var result) ||
            !double.IsFinite(result) || result < 0)
            return null;
        return result;
    }
}
