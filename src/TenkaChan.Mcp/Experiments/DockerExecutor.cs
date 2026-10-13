using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using TenkaChan.Mcp.Security;
using TenkaChan.Mcp.Analysis;

namespace TenkaChan.Mcp.Experiments;

public sealed record ExecutionResult(
    string ExperimentId, string Kind, string Status, int? ExitCode,
    string StartedUtc, string CompletedUtc,
    TestRunSummary? Tests,
    IReadOnlyList<BenchmarkObservation> Benchmarks,
    IReadOnlyList<string> Artifacts,
    string Output, string Stderr, string Note)
{
    public string TargetFramework { get; init; } = "net10.0";
    public NativeCodeReport? NativeDisassembly { get; init; }
}

/// <summary>
/// Runs generated test/benchmark code in an opt-in, resource-constrained Docker
/// container. The source, generated code and NuGet cache are bind-mounted RO.
/// Only a dedicated, per-experiment results folder is writable from the
/// container. There is no host-side execution of generated build scripts.
/// </summary>
public sealed class DockerExecutor
{
    private readonly WorkspaceGuard _guard;
    private readonly ExperimentStore _experiments;

    public DockerExecutor(WorkspaceGuard guard, ExperimentStore experiments)
    {
        _guard = guard;
        _experiments = experiments;
    }

    public async Task<ExecutionResult> RunAsync(string id, CancellationToken cancellationToken)
    {
        if (!_guard.AllowDocker)
            throw new UnauthorizedAccessException(
                "Execution disabled. Set TENKA_ENABLE_DOCKER=1 only after reviewing the sandbox policy.");

        var (manifest, dir) = _experiments.Read(id);
        var artifacts = _experiments.CreateArtifactsDirectory(id);
        var nugetCache = Environment.GetEnvironmentVariable("TENKA_NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget", "packages");
        if (!Directory.Exists(nugetCache))
            throw new DirectoryNotFoundException(
                "Offline NuGet package cache missing. Prewarm dependencies first: " + nugetCache);

        var name = "tenka-" + id;
        var start = DateTimeOffset.UtcNow.ToString("O");
        using var process = new Process { StartInfo = BuildDockerCommand(manifest, dir,
            artifacts, Path.GetFullPath(nugetCache), name) };

        string output = "", stderr = "", note;
        int? exitCode = null;
        string? forcedStatus = null;
        Task<string>? outputTask = null;
        Task<string>? stderrTask = null;
        var launched = false;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(manifest.Kind == "benchmark" ? 8 : 3));
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Docker command failed to start.");
            launched = true;
            outputTask = DrainTail(process.StandardOutput, 16000);
            stderrTask = DrainTail(process.StandardError, 16000);
            await process.WaitForExitAsync(deadline.Token);
            exitCode = process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            forcedStatus = cancellationToken.IsCancellationRequested ? "cancelled" : "timed_out";
        }
        catch (Win32Exception exception)
        {
            forcedStatus = "runner_unavailable";
            stderr = exception.Message;
        }
        catch (InvalidOperationException exception)
        {
            forcedStatus = "runner_unavailable";
            stderr = exception.Message;
        }
        finally
        {
            if (launched && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                try
                {
                    using var settle = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(settle.Token);
                }
                catch (OperationCanceledException) { }
                // Killing the docker CLI does not always stop its container.
                await ForceRemoveContainer(name);
            }
            if (outputTask is not null) output = await outputTask;
            if (stderrTask is not null)
            {
                var captured = await stderrTask;
                stderr = string.IsNullOrEmpty(stderr) ? captured : stderr + "\n" + captured;
            }
        }

        TestRunSummary? tests = null;
        var measurements = new List<BenchmarkObservation>();
        var artifactsFound = new List<string>();
        var parseErrors = new List<string>();
        NativeCodeReport? nativeDisassembly = null;
        var resultDir = new DirectoryInfo(artifacts);

        foreach (var file in resultDir.EnumerateFiles("*", SearchOption.AllDirectories).Take(100))
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var relative = Path.GetRelativePath(artifacts, file.FullName);
            artifactsFound.Add(relative);
            if (manifest.Kind == "test" && file.Extension.Equals(".trx", StringComparison.OrdinalIgnoreCase)
                && tests is null)
            {
                try { tests = ExperimentReportParser.ReadTrx(file.FullName); }
                catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
                {
                    parseErrors.Add("Invalid TRX file: " + ex.Message);
                }
            }
            if (manifest.Kind == "jit" && file.Name == "jit-disassembly.txt")
            {
                try
                {
                    var image = TargetFrameworkSelection.FromValidated(
                        manifest.TargetFramework, "saved").SdkImage;
                    nativeDisassembly = NativeJitDisassemblyParser.Read(file.FullName,
                        manifest.TargetFramework, image, manifest.JitFilter ?? "");
                    if (File.Exists(Path.Combine(artifacts, "jit-truncated.marker")))
                        nativeDisassembly = nativeDisassembly with { Truncated = true };
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException
                    or ArgumentException)
                {
                    parseErrors.Add("Invalid JIT disassembly: " + ex.Message);
                }
            }
            if (manifest.Kind == "benchmark"
                && file.Name.EndsWith("-report-full.json", StringComparison.OrdinalIgnoreCase))
            {
                try { measurements.AddRange(ExperimentReportParser.ReadBenchmarkJson(file.FullName)); }
                catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
                {
                    parseErrors.Add("Invalid BenchmarkDotNet JSON: " + ex.Message);
                }
            }
        }

        string status;
        if (forcedStatus is not null) status = forcedStatus;
        else if (manifest.Kind == "test")
        {
            // VSTest/TRX often emits ResultSummary outcome="Completed" for
            // successful runs; the authoritative counts are in Counters.
            status = tests is null ? "no_test_report"
                : tests.Total <= 0 ? "no_tests_discovered"
                : tests.Failed > 0 ||
                    string.Equals(tests.Outcome, "Failed", StringComparison.OrdinalIgnoreCase)
                    ? "tests_failed"
                : exitCode != 0 ? "test_runner_error"
                : tests.Passed == 0 ? "no_tests_passed"
                : tests.Passed + tests.Skipped < tests.Total ? "test_report_incomplete"
                : string.Equals(tests.Outcome, "Completed", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(tests.Outcome, "Passed", StringComparison.OrdinalIgnoreCase)
                    ? "tests_passed" : "test_report_unrecognized_outcome";
        }
        else if (manifest.Kind == "jit")
        {
            status = exitCode != 0 ? "jit_runner_failed"
                : nativeDisassembly is null ? "jit_report_missing"
                : nativeDisassembly.MethodCount == 0 ||
                  nativeDisassembly.InstructionCount == 0 ? "jit_no_matching_methods"
                : "jit_completed";
        }
        else
        {
            status = exitCode != 0 ? "benchmark_failed"
                : measurements.Count == 0 ||
                  measurements.All(m => m.MeanNanoseconds is not > 0 || m.SampleCount <= 0)
                    ? "benchmark_unverified"
                : measurements.Any(m => m.MeanNanoseconds is not > 0 || m.SampleCount <= 0)
                    ? "benchmark_partial"
                : "benchmark_completed";
        }

        note = "Only the result file from a container is considered observed evidence. " +
            "A passing test does not prove the specification. " +
            "Benchmark summaries are observations, not regression decisions.";
        if (parseErrors.Count > 0) note += " " + string.Join("; ", parseErrors.Take(5));
        var result = new ExecutionResult(id, manifest.Kind, status, exitCode, start,
            DateTimeOffset.UtcNow.ToString("O"), tests, measurements,
            artifactsFound, output, stderr, note)
        {
            TargetFramework = manifest.TargetFramework,
            NativeDisassembly = nativeDisassembly
        };

        _experiments.SaveExecutionResult(result);
        return result;
    }

    private static ProcessStartInfo BuildDockerCommand(ExperimentManifest manifest,
        string directory, string artifacts, string cache, string name)
    {
        var framework = TargetFrameworkSelection.FromValidated(
            manifest.TargetFramework, "manifest");
        var project = manifest.Kind switch
        {
            "test" => "GeneratedTests.csproj",
            "benchmark" => "GeneratedBenchmarks.csproj",
            "jit" => "GeneratedJit.csproj",
            _ => throw new InvalidDataException("Unsupported experiment kind.")
        };

        var run = manifest.Kind switch
        {
            "test" =>
                "dotnet test /work/generated/GeneratedTests.csproj -c Release --no-restore " +
                "--logger 'trx;LogFileName=results.trx' --results-directory /artifacts -p:TargetFramework=" +
                    framework.TargetFramework,
            "benchmark" =>
                "set +e\n" +
                "cd /work/generated\n" +
                "dotnet run -c Release --no-restore " +
                "--project /work/generated/GeneratedBenchmarks.csproj -p:TargetFramework=" +
                    framework.TargetFramework + " -- " +
                "--filter '*' --exporters FullJSON --artifacts /work/benchmark-output --memory\n" +
                "benchmark_exit=$?\nset -e\n" +
                "for report in /work/benchmark-output/results/*-report-full.json; do\n" +
                "  if [ -f \"$report\" ]; then cp \"$report\" /artifacts/; fi\n" +
                "done\nexit \"$benchmark_exit\"",
            "jit" =>
                "dotnet build /work/generated/GeneratedJit.csproj -c Release --no-restore -p:TargetFramework=" +
                    framework.TargetFramework + "\n" +
                "set +e\n" +
                "DOTNET_JitDisasm=\"$TENKA_JIT_FILTER\" " +
                "DOTNET_JitDisasmWithCodeBytes=1 " +
                "DOTNET_JitStdOutFile=/work/jit-native.txt " +
                "DOTNET_ReadyToRun=0 DOTNET_TieredCompilation=0 " +
                "dotnet /work/generated/bin/Release/" + framework.TargetFramework +
                "/GeneratedJit.dll\n" +
                "jit_exit=$?\nset -e\n" +
                "if [ -f /work/jit-native.txt ]; then\n" +
                "  jit_size=$(wc -c < /work/jit-native.txt)\n" +
                "  if [ \"$jit_size\" -gt 5000000 ]; then touch /artifacts/jit-truncated.marker; fi\n" +
                "  head -c 5000000 /work/jit-native.txt > /artifacts/jit-disassembly.txt\n" +
                "fi\nexit \"$jit_exit\"",
            _ => throw new InvalidDataException("Unsupported experiment kind.")
        };

        var script =
            "set -eu\n" +
            "mkdir -p /work/repo /work/generated\n" +
            "tar -C /input --exclude=.git --exclude=bin --exclude=obj -cf - . | " +
                "tar -C /work/repo -xf -\n" +
            "cp -R /generated/. /work/generated/\n" +
            "printf '<configuration><packageSources><clear/></packageSources></configuration>' " +
                "> /work/NuGet.Config\n" +
            "dotnet restore /work/generated/" + project +
                " --configfile /work/NuGet.Config -p:NuGetAudit=false -p:TargetFramework=" +
                    framework.TargetFramework + "\n" +
            run + "\n";

        var info = new ProcessStartInfo("docker")
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var arg in new[]
        {
            "run", "--rm", "--pull=never", "--name", name,
            "--network", "none", "--read-only", "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--pids-limit", "128", "--memory", "2g", "--cpus", "2",
            "--user", "65532:65532",
            "--tmpfs", "/work:rw,exec,size=1g,mode=1777",
            "--tmpfs", "/tmp:rw,exec,size=128m,mode=1777",
            "--env", "HOME=/work", "--env", "DOTNET_CLI_HOME=/work",
            "--env", "DOTNET_NOLOGO=1",
            "--env", "DOTNET_CLI_TELEMETRY_OPTOUT=1",
            "--env", "NUGET_PACKAGES=/nuget",
            "--mount", "type=bind,source=" + manifest.RepositoryRoot + ",target=/input,readonly",
            "--mount", "type=bind,source=" + directory + ",target=/generated,readonly",
            "--mount", "type=bind,source=" + cache + ",target=/nuget,readonly",
            "--mount", "type=bind,source=" + artifacts + ",target=/artifacts"
        }) info.ArgumentList.Add(arg);
        if (manifest.Kind == "jit")
        {
            info.ArgumentList.Add("--env");
            info.ArgumentList.Add("TENKA_JIT_FILTER=" +
                TargetFrameworkPolicy.ValidateJitFilter(manifest.JitFilter ?? ""));
        }
        foreach (var arg in new[] { framework.SdkImage, "sh", "-c", script })
            info.ArgumentList.Add(arg);
        return info;
    }

    private static async Task<string> DrainTail(StreamReader stream, int keepCharacters)
    {
        var tail = new StringBuilder(keepCharacters + 4096);
        var buffer = new char[4096];
        int n;
        while ((n = await stream.ReadAsync(buffer)) > 0)
        {
            tail.Append(buffer, 0, n);
            if (tail.Length > keepCharacters)
                tail.Remove(0, tail.Length - keepCharacters);
        }
        return tail.ToString();
    }

    private static async Task ForceRemoveContainer(string name)
    {
        try
        {
            using var cleanup = new Process
            {
                StartInfo = new ProcessStartInfo("docker")
                {
                    UseShellExecute = false, RedirectStandardOutput = false,
                    RedirectStandardError = false, CreateNoWindow = true
                }
            };
            cleanup.StartInfo.ArgumentList.Add("rm");
            cleanup.StartInfo.ArgumentList.Add("-f");
            cleanup.StartInfo.ArgumentList.Add(name);
            if (!cleanup.Start()) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
            await cleanup.WaitForExitAsync(timeout.Token);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
            or OperationCanceledException)
        {
            // Cleanup best effort. The runtime should be monitored for leaked
            // containers when untrusted workloads are allowed.
        }
    }
}
