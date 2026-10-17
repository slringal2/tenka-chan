using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Experiments;

/// <summary>
/// UNSANDBOXED runner, intentionally separate from DockerExecutor. Never enabled
/// solely by having the normal trusted-app execution flag turned on.
/// </summary>
public sealed class TrustedHostExperimentExecutor(
    WorkspaceGuard guard, ExperimentStore experiments, WorkspaceBinaryDiscovery discovery)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public bool Enabled => Environment.GetEnvironmentVariable("TENKA_ENABLE_LOCAL_EXEC") == "1"
        && Environment.GetEnvironmentVariable("TENKA_ENABLE_UNSANDBOXED_EXPERIMENTS") == "1";

    public async Task<ExecutionResult> RunAsync(string experimentId,
        bool approveUnisolatedExecution, CancellationToken ct = default)
    {
        if (!Enabled || !approveUnisolatedExecution)
            throw new UnauthorizedAccessException(
                "Host execution of generated code is UNSANDBOXED. Both TENKA_ENABLE_LOCAL_EXEC=1 " +
                "and TENKA_ENABLE_UNSANDBOXED_EXPERIMENTS=1 plus explicit per-run approval are required.");
        if (!await Gate.WaitAsync(0, ct))
            throw new InvalidOperationException("Another generated host experiment is running.");
        try
        {
            var (manifest, dir) = experiments.Read(experimentId);
            if (manifest.Kind is not ("test" or "benchmark"))
                throw new ArgumentException("Host generated experiment runner supports xUnit and BenchmarkDotNet only.");
            if (File.Exists(Path.Combine(dir, "execution-report.json")))
                throw new InvalidOperationException("Experiment has already been executed. Prepare a new one.");
            // CreateNew also rejects concurrent executions by a second Tenka process.
            using var lockFile = new FileStream(Path.Combine(dir, "host-execution.lock"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            var subjectFramework = manifest.SubjectTargetFramework ?? manifest.TargetFramework;
            var selected = discovery.RequireBuiltExperimentReference(
                Path.Combine(manifest.RepositoryRoot, manifest.ProjectRelativePath),
                subjectFramework, manifest.TargetFramework);
            var source = guard.RequireFile(selected.AssemblyPath, ".dll");
            var cache = Environment.GetEnvironmentVariable("TENKA_NUGET_PACKAGES")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
            if (!Directory.Exists(cache))
                throw new DirectoryNotFoundException("NuGet package cache is missing. Restore trusted dependencies first.");

            // Resolve the dependency closure BEFORE creating any staging directories,
            // so a missing/ambiguous build reference can be fixed without poisoning a
            // prepared experiment ID or accidentally causing a partial second run.
            var projectFile = manifest.Kind == "test" ? "GeneratedTests.csproj" : "GeneratedBenchmarks.csproj";
            var project = XDocument.Load(Path.Combine(dir, projectFile));
            // Keep the execution surface to already-built assemblies. Explicitly
            // reference the subject's local PE dependency closure at COMPILE time;
            // copying dependency DLLs after build is too late for public API types.
            // Never leave ProjectReference: that could execute target MSBuild targets.
            var referenceCount = TrustedExperimentReferenceClosure.ReplaceProjectReferences(project, guard, source);
            var work = Path.Combine(dir, "host-work");
            if (Directory.Exists(work)) throw new IOException("Host staging directory already exists.");
            Directory.CreateDirectory(work);
            var artifacts = Path.Combine(dir, "host-artifacts");
            Directory.CreateDirectory(artifacts);
            var stagedProject = Path.Combine(work, projectFile);
            project.Save(stagedProject);
            File.Copy(Path.Combine(dir, "Generated.cs"), Path.Combine(work, "Generated.cs"));
            if (manifest.Kind == "benchmark")
                File.Copy(Path.Combine(dir, "Program.cs"), Path.Combine(work, "Program.cs"));
            // Restore exclusively from already cached packages. No NuGet network sources.
            var config = Path.Combine(work, "NuGet.Config");
            File.WriteAllText(config,
                "<configuration><packageSources><clear /></packageSources></configuration>");
            var start = DateTimeOffset.UtcNow.ToString("O");
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            int? exit = null;
            string status = "host_runner_failed";
            try
            {
                var restore = await Step(work, 75, ct, ["restore", stagedProject,
                    "--configfile", config, "-p:NuGetAudit=false"]);
                Append(stdout, restore.Stdout); Append(stderr, restore.Stderr);
                if (restore.ExitCode != 0) { exit = restore.ExitCode; status = "restore_failed_offline"; }
                else
                {
                    var build = await Step(work, 90, ct,
                        ["build", stagedProject, "-c", "Release", "--no-restore"]);
                    Append(stdout, build.Stdout); Append(stderr, build.Stderr);
                    if (build.ExitCode != 0) { exit = build.ExitCode; status = "build_failed"; }
                    else
                    {
                        // Copy already-built private DLL dependencies from the selected
                        // application output. Never load or execute them in this stage.
                        CopyDependencies(source, Path.Combine(work, "bin", "Release",
                            manifest.TargetFramework), guard);
                        var args = manifest.Kind == "test"
                            ? new[] { "test", stagedProject, "-c", "Release", "--no-build",
                                "--no-restore", "--logger", "trx;LogFileName=results.trx",
                                "--results-directory", artifacts }
                            : new[] { "run", "--no-build", "--no-restore", "-c", "Release",
                                "--project", stagedProject, "--",
                                "--filter", "*", "--exporters", "FullJSON",
                                "--artifacts", artifacts, "--memory" };
                        var run = await Step(work, manifest.Kind == "test" ? 120 : 420, ct, args);
                        Append(stdout, run.Stdout); Append(stderr, run.Stderr);
                        exit = run.ExitCode;
                        status = "completed_without_verified_report";
                    }
                }
            }
            catch (OperationCanceledException)
            {
                status = ct.IsCancellationRequested ? "cancelled" : "timed_out";
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
            { status = "runner_unavailable"; Append(stderr, ex.Message); }

            TestRunSummary? tests = null;
            var benchmarks = new List<BenchmarkObservation>();
            var outputFiles = new List<string>();
            var errors = new List<string>();
            foreach (var file in Directory.EnumerateFiles(artifacts, "*", SearchOption.AllDirectories).Take(100))
            {
                var info = new FileInfo(file);
                if (info.Length > 10_000_000 || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                outputFiles.Add(Path.GetRelativePath(artifacts, file));
                try
                {
                    if (manifest.Kind == "test" && file.EndsWith(".trx", StringComparison.OrdinalIgnoreCase))
                        tests ??= ExperimentReportParser.ReadTrx(file);
                    else if (manifest.Kind == "benchmark" &&
                        file.EndsWith("-report-full.json", StringComparison.OrdinalIgnoreCase))
                        benchmarks.AddRange(ExperimentReportParser.ReadBenchmarkJson(file));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException
                    or System.Xml.XmlException or System.Text.Json.JsonException
                    or ArgumentException)
                { errors.Add(Path.GetFileName(file) + ": " + ex.Message); }
            }
            if (status == "completed_without_verified_report")
            {
                status = manifest.Kind == "test"
                    ? tests is null ? "no_test_report"
                        : tests.Failed > 0 ? "tests_failed"
                        : tests.Total == 0 ? "no_tests_discovered"
                        : exit == 0 && tests.Passed > 0 ? "tests_passed" : "test_runner_error"
                    : exit != 0 ? "benchmark_failed"
                        : benchmarks.Count > 0 && benchmarks.All(x => x.MeanNanoseconds is > 0 && x.SampleCount > 0)
                            ? "benchmark_completed" : "benchmark_unverified";
            }
            var result = new ExecutionResult(experimentId, manifest.Kind, status, exit,
                start, DateTimeOffset.UtcNow.ToString("O"), tests,
                benchmarks.Take(200).ToArray(), outputFiles.Take(100).ToArray(),
                Tail(stdout.ToString(), 12000), Tail(stderr.ToString(), 12000),
                "UNSANDBOXED HOST WORKFLOW: build/test code may execute with current OS user permissions. See status for completed stages. " +
                "No Docker isolation; even cached packages, test discovery or build targets can execute code. " +
                "Offline restore does NOT block network operations inside generated programs. " +
                $"Subject framework: {subjectFramework}; experiment runtime: {manifest.TargetFramework}. " +
                $"Already-built local compile references: {referenceCount}; no subject project was rebuilt. " +
                "Framework selection is not proof of a production-matched environment. " +
                "The reference was prepared from an existing allowlisted DLL; Tenka did not retarget the source project. " +
                "Time limits and child-tree termination are best effort, not security boundaries. " +
                string.Join("; ", errors.Take(4)))
            { TargetFramework = manifest.TargetFramework };
            experiments.SaveExecutionResult(result);
            return result;
        }
        finally { Gate.Release(); }
    }

    private static void CopyDependencies(string original, string targetDir, WorkspaceGuard guard)
    {
        Directory.CreateDirectory(targetDir);
        var root = Path.GetDirectoryName(original)!;
        var totalBytes = 0L;
        foreach (var path in Directory.EnumerateFiles(root, "*.dll").Take(120))
        {
            if (Path.GetFullPath(path) == Path.GetFullPath(original)) continue;
            guard.RequireFile(path, ".dll");
            var info = new FileInfo(path);
            if (info.Length > 16_000_000 || totalBytes + info.Length > 100_000_000) break;
            var dest = Path.Combine(targetDir, info.Name);
            if (File.Exists(dest)) continue; // Preserve generated test dependencies.
            File.Copy(path, dest);
            totalBytes += info.Length;
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> Step(
        string workingDirectory, int seconds, CancellationToken ct, string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        // Do not pass unrelated cloud credentials through ambient tool variables.
        psi.Environment.Clear();
        foreach (var key in new[] { "PATH", "SystemRoot", "WINDIR", "COMSPEC",
            "HOME", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "TEMP", "TMP", "TMPDIR",
            "DOTNET_ROOT", "DOTNET_ROOT_X64", "ProgramFiles", "ProgramFiles(x86)" })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(value)) psi.Environment[key] = value;
        }
        psi.Environment["DOTNET_CLI_HOME"] = workingDirectory;
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        // DOTNET_CLI_HOME is intentionally redirected to staging. NuGet must still
        // look in the operator's prewarmed, user-scoped global package cache.
        var cache = Environment.GetEnvironmentVariable("TENKA_NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget", "packages");
        psi.Environment["NUGET_PACKAGES"] = Path.GetFullPath(cache);
        using var process = new Process { StartInfo = psi };
        if (!process.Start()) throw new InvalidOperationException("Could not launch the installed dotnet CLI.");
        var output = Drain(process.StandardOutput, 12000);
        var error = Drain(process.StandardError, 12000);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }
            throw;
        }
        return (process.ExitCode, await output, await error);
    }

    private static async Task<string> Drain(StreamReader stream, int maxLength)
    {
        var tail = new StringBuilder();
        var chars = new char[2048];
        int n;
        while ((n = await stream.ReadAsync(chars)) > 0)
        {
            tail.Append(chars, 0, n);
            if (tail.Length > maxLength) tail.Remove(0, tail.Length - maxLength);
        }
        return tail.ToString();
    }

    private static string Tail(string value, int count) =>
        value.Length <= count ? value : value[^count..];
    private static void Append(StringBuilder buffer, string text)
    {
        buffer.Append(text);
        if (buffer.Length > 16000) buffer.Remove(0, buffer.Length - 16000);
    }
}
