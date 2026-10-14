using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using TenkaChan.Mcp.Security;
using TenkaChan.Mcp.Analysis;

namespace TenkaChan.Mcp.Experiments;

public sealed record ExecutionResult(string ExperimentId, string Kind, string Status, int? ExitCode,
    string StartedUtc, string CompletedUtc, TestRunSummary? Tests,
    IReadOnlyList<BenchmarkObservation> Benchmarks, IReadOnlyList<string> Artifacts,
    string Output, string Stderr, string Note)
{
    public string TargetFramework { get; init; } = "net10.0";
    public NativeCodeReport? NativeDisassembly { get; init; }
    public IReadOnlyList<NativeBinaryArtifact> BinaryArtifacts { get; init; } = [];
    public bool BinaryCaptureTruncated { get; init; }
    public string? RuntimeInfo { get; init; }
}

/// <summary>Explicitly authorized offline container execution. Experiment IDs are single-use.</summary>
public sealed class DockerExecutor(WorkspaceGuard guard, ExperimentStore experiments)
{
    public async Task<ExecutionResult> RunAsync(string id, CancellationToken cancellationToken)
    {
        if (!guard.AllowDocker)
            throw new UnauthorizedAccessException("Execution disabled. Set TENKA_ENABLE_DOCKER=1 after reviewing the sandbox policy.");
        var (manifest, dir) = experiments.Read(id);
        var artifacts = experiments.CreateArtifactsDirectory(id);
        var cache = Environment.GetEnvironmentVariable("TENKA_NUGET_PACKAGES") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        if (!Directory.Exists(cache)) throw new DirectoryNotFoundException("Prewarm the offline NuGet cache first: " + cache);
        using var reservation = ExperimentExecutionReservation.Acquire(dir, artifacts);
        var name = "tenka-" + id;
        var start = DateTimeOffset.UtcNow.ToString("O");
        using var process = new Process { StartInfo = BuildDockerCommand(manifest, dir, artifacts, Path.GetFullPath(cache), name) };
        string output = "", error = "";
        int? exit = null;
        string? forced = null;
        Task<string>? stdout = null, stderr = null;
        var launched = false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(manifest.Kind == "benchmark" ? 8 : 3));
        try
        {
            launched = process.Start();
            if (!launched) throw new InvalidOperationException("Docker process did not start.");
            stdout = DrainTail(process.StandardOutput, 16000);
            stderr = DrainTail(process.StandardError, 16000);
            await process.WaitForExitAsync(timeout.Token);
            exit = process.ExitCode;
        }
        catch (OperationCanceledException) { forced = cancellationToken.IsCancellationRequested ? "cancelled" : "timed_out"; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        { forced = "runner_unavailable"; error = ex.Message; }
        finally
        {
            if (launched && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
            }
            if (launched && forced is not null) await ForceRemoveContainer(name);
            if (stdout is not null) output = await stdout;
            if (stderr is not null) error += await stderr;
        }

        TestRunSummary? tests = null;
        NativeCodeReport? native = null;
        var metrics = new List<BenchmarkObservation>();
        var names = new List<string>();
        var errors = new List<string>();
        var artifactGuard = new WorkspaceGuard([artifacts], false, false);
        foreach (var file in new DirectoryInfo(artifacts).EnumerateFiles("*", SearchOption.TopDirectoryOnly).Take(100))
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var safe = artifactGuard.RequireFile(file.FullName, file.Extension);
            names.Add(file.Name);
            try
            {
                if (manifest.Kind == "test" && file.Extension == ".trx" && tests is null)
                    tests = ExperimentReportParser.ReadTrx(safe);
                if (manifest.Kind == "benchmark" && file.Name.EndsWith("-report-full.json", StringComparison.Ordinal))
                    metrics.AddRange(ExperimentReportParser.ReadBenchmarkJson(safe));
                if (manifest.Kind == "jit" && file.Name == "jit-disassembly.txt")
                {
                    native = NativeJitDisassemblyParser.Read(safe, manifest.TargetFramework,
                        TargetFrameworkSelection.FromValidated(manifest.TargetFramework, "saved").SdkImage,
                        manifest.JitFilter ?? "");
                    if (File.Exists(Path.Combine(artifacts, "jit-truncated.marker"))) native = native with { Truncated = true };
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or
                System.Text.Json.JsonException or System.Xml.XmlException)
            { errors.Add(file.Name + ": " + ex.Message); }
        }
        IReadOnlyList<NativeBinaryArtifact> binaries = [];
        string? runtimeInfo = null;
        if (manifest.Kind == "jit")
        {
            try
            {
                binaries = NativeBinaryArtifacts.Snapshot(artifacts);
                var path = Path.Combine(artifacts, "runtime-info.txt");
                if (File.Exists(path)) runtimeInfo = Encoding.UTF8.GetString(
                    BoundedBinaryFile.Read(artifactGuard.RequireFile(path, ".txt"), 32000));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            { errors.Add("Build artifact capture: " + ex.Message); }
        }
        var status = forced ?? (manifest.Kind switch
        {
            "test" => tests is null ? "no_test_report"
                : tests.Total <= 0 ? "no_tests_discovered"
                : tests.Failed > 0 || tests.Outcome.Equals("Failed", StringComparison.OrdinalIgnoreCase) ? "tests_failed"
                : exit != 0 ? "test_runner_error" : tests.Passed == 0 ? "no_tests_passed"
                : tests.Passed + tests.Skipped < tests.Total ? "test_report_incomplete"
                : tests.Outcome.Equals("Completed", StringComparison.OrdinalIgnoreCase) || tests.Outcome.Equals("Passed", StringComparison.OrdinalIgnoreCase)
                    ? "tests_passed" : "test_report_unrecognized_outcome",
            "jit" => exit != 0 ? "jit_runner_failed" : native is null ? "jit_report_missing"
                : native.MethodCount == 0 || native.InstructionCount == 0 ? "jit_no_matching_methods" : "jit_completed",
            _ => exit != 0 ? "benchmark_failed"
                : metrics.Count == 0 || metrics.All(m => m.MeanNanoseconds is not > 0 || m.SampleCount <= 0) ? "benchmark_unverified"
                : metrics.Any(m => m.MeanNanoseconds is not > 0 || m.SampleCount <= 0) ? "benchmark_partial" : "benchmark_completed"
        });
        var result = new ExecutionResult(id, manifest.Kind, status, exit, start, DateTimeOffset.UtcNow.ToString("O"),
            tests, metrics, names, output, error,
            "Container artifacts are observations, not a proof of correctness or speed. Captured binaries belong to this run; never silently substitute a later build. " +
            string.Join("; ", errors.Take(5)))
        {
            TargetFramework = manifest.TargetFramework, NativeDisassembly = native,
            BinaryArtifacts = binaries, RuntimeInfo = runtimeInfo,
            BinaryCaptureTruncated = manifest.Kind == "jit" &&
                (binaries.Count == 0 || File.Exists(Path.Combine(artifacts, "binary-capture-truncated.marker")))
        };
        experiments.SaveExecutionResult(result);
        return result;
    }

    private static ProcessStartInfo BuildDockerCommand(ExperimentManifest manifest,
        string directory, string artifacts, string cache, string name)
    {
        var target = TargetFrameworkSelection.FromValidated(manifest.TargetFramework, "manifest");
        var project = manifest.Kind switch
        {
            "test" => "GeneratedTests.csproj", "benchmark" => "GeneratedBenchmarks.csproj", "jit" => "GeneratedJit.csproj",
            _ => throw new InvalidDataException("Unsupported experiment kind.")
        };
        var run = manifest.Kind switch
        {
            "test" => "dotnet test /work/generated/GeneratedTests.csproj -c Release --no-restore " +
                "--logger 'trx;LogFileName=results.trx' --results-directory /artifacts -p:TargetFramework=" + target.TargetFramework,
            "benchmark" => "set +e\ncd /work/generated\n" +
                "dotnet run -c Release --no-restore --project /work/generated/GeneratedBenchmarks.csproj -p:TargetFramework=" + target.TargetFramework +
                " -- --filter '*' --exporters FullJSON --artifacts /work/benchmark-output --memory\n" +
                "benchmark_exit=$?\nset -e\nfor report in /work/benchmark-output/results/*-report-full.json; do\n" +
                "  if [ -f \"$report\" ]; then cp \"$report\" /artifacts/; fi\ndone\nexit \"$benchmark_exit\"",
            "jit" => "dotnet build /work/generated/GeneratedJit.csproj -c Release --no-restore -p:TargetFramework=" + target.TargetFramework + "\n" +
                NativeBinaryArtifacts.CaptureScript(target.TargetFramework) +
                "set +e\nDOTNET_JitDisasm=\"$TENKA_JIT_FILTER\" DOTNET_JitDisasmWithCodeBytes=1 " +
                "DOTNET_JitStdOutFile=/work/jit-native.txt DOTNET_ReadyToRun=0 DOTNET_TieredCompilation=0 " +
                "dotnet /work/generated/bin/Release/" + target.TargetFramework + "/GeneratedJit.dll\n" +
                "jit_exit=$?\nset -e\nif [ -f /work/jit-native.txt ]; then\n" +
                "  jit_size=$(wc -c < /work/jit-native.txt)\n" +
                "  if [ \"$jit_size\" -gt 5000000 ]; then touch /artifacts/jit-truncated.marker; fi\n" +
                "  head -c 5000000 /work/jit-native.txt > /artifacts/jit-disassembly.txt\nfi\nexit \"$jit_exit\"",
            _ => throw new InvalidDataException("Unsupported kind.")
        };
        var script = "set -eu\nmkdir -p /work/repo /work/generated /work/temp /work/.local/share /work/.config\n" +
            "tar -C /input --exclude=.git --exclude=bin --exclude=obj -cf - . | tar -C /work/repo -xf -\n" +
            "cp -R /generated/. /work/generated/\n" +
            "printf '<configuration><packageSources><clear/></packageSources></configuration>' > /work/NuGet.Config\n" +
            "dotnet restore /work/generated/" + project + " --configfile /work/NuGet.Config -p:NuGetAudit=false -p:TargetFramework=" +
            target.TargetFramework + "\n" + run + "\n";
        var info = new ProcessStartInfo("docker")
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in new[]
        {
            "run", "--rm", "--pull=never", "--name", name, "--network", "none", "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges", "--pids-limit", "128", "--memory", "2g", "--cpus", "2", "--user", "1654:1654",
            "--tmpfs", "/work:rw,exec,size=1g,mode=1777", "--tmpfs", "/tmp:rw,exec,size=128m,mode=1777",
            "--tmpfs", "/dev/shm:rw,nosuid,nodev,size=64m,mode=1777",
            "--env", "HOME=/work", "--env", "DOTNET_CLI_HOME=/work", "--env", "TMPDIR=/work/temp", "--env", "DOTNET_NOLOGO=1",
            "--env", "XDG_DATA_HOME=/work/.local/share",
            "--env", "XDG_CONFIG_HOME=/work/.config",
            "--env", "DOTNET_CLI_TELEMETRY_OPTOUT=1",
            "--env", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1",
            "--env", "DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1",
            "--env", "NUGET_PACKAGES=/nuget",
            "--mount", "type=bind,source=" + manifest.RepositoryRoot + ",target=/input,readonly",
            "--mount", "type=bind,source=" + directory + ",target=/generated,readonly",
            "--mount", "type=bind,source=" + cache + ",target=/nuget,readonly",
            "--mount", "type=bind,source=" + artifacts + ",target=/artifacts"
        }) info.ArgumentList.Add(arg);
        // The .NET 8 SDK may require its own ephemeral filesystem for the
        // NuGet-Migrations named mutex even when HOME,/tmp,/work are writable.
        // A writable container overlay is NOT a writable host source mount:
        // source, generated experiment and package cache remain bind-mounted
        // read-only; network/caps/privilege protections still apply.
        // net10.0 has already been verified with an immutable root layer.
        if (target.MajorVersion == 10)
            info.ArgumentList.Add("--read-only");
        if (manifest.Kind == "jit")
        { info.ArgumentList.Add("--env"); info.ArgumentList.Add("TENKA_JIT_FILTER=" + TargetFrameworkPolicy.ValidateJitFilter(manifest.JitFilter ?? "")); }
        foreach (var arg in new[] { target.SdkImage, "sh", "-c", script }) info.ArgumentList.Add(arg);
        return info;
    }

    private static async Task<string> DrainTail(StreamReader stream, int limit)
    {
        var tail = new StringBuilder();
        var buffer = new char[4096];
        int n;
        while ((n = await stream.ReadAsync(buffer)) > 0)
        { tail.Append(buffer, 0, n); if (tail.Length > limit) tail.Remove(0, tail.Length - limit); }
        return tail.ToString();
    }
    private static async Task ForceRemoveContainer(string name)
    {
        try
        {
            using var cleanup = new Process { StartInfo = new ProcessStartInfo("docker")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
            foreach (var arg in new[] { "rm", "-f", name }) cleanup.StartInfo.ArgumentList.Add(arg);
            if (!cleanup.Start()) return;
            var stdout = DrainTail(cleanup.StandardOutput, 1000);
            var stderr = DrainTail(cleanup.StandardError, 1000);
            try { await cleanup.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(7)); }
            catch (TimeoutException) { if (!cleanup.HasExited) cleanup.Kill(true); }
            await Task.WhenAll(stdout, stderr);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException) { }
    }
}
