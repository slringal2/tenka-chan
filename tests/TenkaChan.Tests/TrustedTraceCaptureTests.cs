using System.Text.Json;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class TrustedTraceCaptureTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task BothExecutionOptInsAreRequired(bool enabled, bool trusted)
    {
        var runner = new TrustedTraceCapture(new WorkspaceGuard([], false, false), enabled, null);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => runner.CaptureAsync("app.dll", ".", trusted));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public void RejectsInvalidTimeouts(int timeout) => Assert.Throws<ArgumentOutOfRangeException>(() =>
        TrustedTraceCapture.ValidateArguments([], timeout));

    [Theory]
    [InlineData("bad\nargument")]
    [InlineData("bad\rargument")]
    [InlineData("bad\0argument")]
    public void RejectsControlCharacters(string arg) => Assert.Throws<ArgumentException>(() =>
        TrustedTraceCapture.ValidateArguments([arg], 30));

    [Fact]
    public void CommandUsesArgumentListAndStripsInheritedStartupHooks()
    {
        var command = TrustedTraceCapture.BuildCollectCommand("/tools/dotnet-trace", "/tools/dotnet",
            "/allowed dir/app.dll", "/captures/run", ["a;echo injected", "--providers", "something"]);
        Assert.False(command.UseShellExecute);
        Assert.Empty(command.Arguments);
        Assert.Equal("collect", command.ArgumentList[0]);
        var index = command.ArgumentList.IndexOf("--");
        Assert.True(index > 0);
        Assert.Equal("/tools/dotnet", command.ArgumentList[index + 1]);
        Assert.Equal("exec", command.ArgumentList[index + 2]);
        Assert.Equal("/allowed dir/app.dll", command.ArgumentList[index + 3]);
        Assert.Equal("a;echo injected", command.ArgumentList[index + 4]);
        Assert.False(command.Environment.ContainsKey("DOTNET_STARTUP_HOOKS"));
        Assert.False(command.Environment.ContainsKey("COR_ENABLE_PROFILING"));
    }

    [Fact]
    public void PreservesAllThreadChoicesInsteadOfSilentlySelectingFirst()
    {
        var dir = Path.Combine(Path.GetTempPath(), "trace-choice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "profile.json");
            File.WriteAllText(path, """
                {"profiles":[
                  {"name":"waiting","type":"evented","unit":"milliseconds","events":[]},
                  {"name":"worker","type":"sampled","unit":"milliseconds","samples":[[0]]}
                ]}
                """);
            var choices = TrustedTraceCapture.ReadProfileChoices(path);
            Assert.Equal(2, choices.Count);
            Assert.Equal(0, choices[0].Index);
            Assert.Equal(1, choices[1].Index);
            Assert.Equal("worker", choices[1].Name);
            Assert.Equal(1, choices[1].Observations);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SavedArtifactChangesAndPartialCapturesAreRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "trace-integrity-" + Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid().ToString("N");
        var folder = Path.Combine(root, "tenka-trace-" + id);
        Directory.CreateDirectory(folder);
        try
        {
            var trace = Path.Combine(folder, "workload.nettrace");
            var profile = Path.Combine(folder, "workload.speedscope.json");
            var manifest = Path.Combine(folder, "capture.json");
            File.WriteAllText(trace, "test bytes, not a runtime capture");
            File.WriteAllText(profile, """{"profiles":[{"type":"sampled","unit":"none","samples":[[]]}]}""");
            var record = new TraceCaptureReport(id, "trace_completed", manifest, "app.dll", "net8.0",
                "eventpipe_sampled_thread_time", "start", "end", "tool", "test", 0, 0, [], [],
                TrustedTraceCapture.Fingerprint(trace, 1024), TrustedTraceCapture.Fingerprint(profile, 1024), [], "", []);
            File.WriteAllText(manifest, JsonSerializer.Serialize(record, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var reader = new TrustedTraceCapture(new WorkspaceGuard([root], false, false), false, null);
            Assert.Equal(id, reader.Read(manifest).Id); // No execution permission needed to read.
            Assert.Throws<InvalidOperationException>(() => reader.RequireProfile(record with { Status = "timed_out" }, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => reader.RequireProfile(record, 99));
            File.AppendAllText(trace, "changed");
            Assert.Throws<InvalidDataException>(() => reader.Read(manifest));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CapturesRealNet8WorkloadAndMapsBusyMethod()
    {
        if (Environment.GetEnvironmentVariable("TENKA_TRACE_INTEGRATION") != "1") return;
        var fixture = Environment.GetEnvironmentVariable("TENKA_TRACE_FIXTURE")!;
        var root = Path.Combine(Path.GetTempPath(), "tenka-trace-it-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var guard = new WorkspaceGuard([root, Path.GetDirectoryName(fixture)!], false, false);
            var collector = new TrustedTraceCapture(guard, true, Environment.GetEnvironmentVariable("TENKA_TEST_TRACE_TOOL"));
            var capture = await collector.CaptureAsync(fixture, root, true, 90);
            Assert.True(capture.Status == "trace_completed", capture.Status + "\n" + capture.Output);
            Assert.Equal("eventpipe_sampled_thread_time", capture.MeasurementKind);
            Assert.NotNull(capture.Inputs[0].Mvid);
            Assert.NotEmpty(capture.Profiles);
            var saved = collector.Read(capture.ReportPath);
            Assert.True(collector.CompareBinary(saved.ReportPath, fixture).SameRecordedInputs);
            var found = false;
            foreach (var choice in saved.Profiles.Where(p => p.Observations > 0))
            {
                var profile = SpeedscopeHotspotParser.Read(collector.RequireProfile(saved, choice.Index),
                    [new ProfileMethodIdentity("busy", "TraceFixture.Workload.Busy")], choice.Index, 500);
                found |= profile.Frames.Any(f => f.MatchedMethodId == "busy" && f.InclusiveWeight > 0);
            }
            Assert.True(found, "The real .NET 8 sampled profile must contain the controlled non-inlined busy method.");
            var runtime = Path.ChangeExtension(fixture, ".runtimeconfig.json");
            var original = File.ReadAllText(runtime);
            try
            {
                File.AppendAllText(runtime, " ");
                Assert.False(collector.CompareBinary(saved.ReportPath, fixture).SameRecordedInputs);
            }
            finally { File.WriteAllText(runtime, original); }
            var timed = await collector.CaptureAsync(fixture, root, true, 2, ["--sleep"]);
            Assert.Equal("timed_out", timed.Status);
            Assert.Throws<InvalidOperationException>(() => collector.RequireProfile(timed, 0));
            File.AppendAllText(Path.Combine(Path.GetDirectoryName(capture.ReportPath)!, "workload.speedscope.json"), " ");
            Assert.Throws<InvalidDataException>(() => collector.Read(capture.ReportPath));
        }
        finally { Directory.Delete(root, true); }
    }
}
