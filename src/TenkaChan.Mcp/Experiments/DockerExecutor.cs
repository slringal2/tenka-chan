using System.Diagnostics;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Experiments;

public sealed record ExecutionResult(string ExperimentId, string Status, int? ExitCode,
    string Output, string Stderr, string Note);

/// <summary>
/// Opt-in offline Docker execution. Never executes generated C# or MSBuild
/// scripts directly in the MCP host process.
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
                "Execution disabled. Set TENKA_ENABLE_DOCKER=1 after reviewing the sandbox policy.");
        var (manifest, dir) = _experiments.Read(id);
        var cache = Environment.GetEnvironmentVariable("TENKA_NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        if (!Directory.Exists(cache))
            throw new DirectoryNotFoundException(
                "Offline NuGet package cache missing. Prewarm packages on the trusted host first: " + cache);

        var project = manifest.Kind == "test" ? "GeneratedTests.csproj" : "GeneratedBenchmarks.csproj";
        var run = manifest.Kind == "test"
            ? "dotnet test /work/generated/GeneratedTests.csproj -c Release --no-restore --logger 'console;verbosity=normal'"
            : "dotnet run -c Release --no-restore --project /work/generated/GeneratedBenchmarks.csproj -- --filter '*'";
        var script = "set -eu\n"
            + "mkdir -p /work/repo /work/generated\n"
            + "cp -R /input/. /work/repo/\n"
            + "cp -R /generated/. /work/generated/\n"
            + "printf '<configuration><packageSources><clear/></packageSources></configuration>' > /work/NuGet.Config\n"
            + "dotnet restore /work/generated/" + project + " --configfile /work/NuGet.Config -p:NuGetAudit=false\n"
            + run + "\n";

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo("docker")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in new[]
        {
            "run", "--rm", "--network", "none", "--read-only", "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges", "--pids-limit", "128",
            "--memory", "2g", "--cpus", "2", "--user", "65532:65532",
            "--tmpfs", "/work:rw,exec,size=1g,mode=1777",
            "--tmpfs", "/tmp:rw,exec,size=128m,mode=1777",
            "--env", "HOME=/work", "--env", "NUGET_PACKAGES=/nuget",
            "--mount", "type=bind,source=" + manifest.RepositoryRoot + ",target=/input,readonly",
            "--mount", "type=bind,source=" + dir + ",target=/generated,readonly",
            "--mount", "type=bind,source=" + Path.GetFullPath(cache) + ",target=/nuget,readonly",
            "mcr.microsoft.com/dotnet/sdk:10.0", "sh", "-c", script
        })
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(manifest.Kind == "test" ? 3 : 8));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            var error = await stderr;
            return new ExecutionResult(id, process.ExitCode == 0 ? "completed" : "failed",
                process.ExitCode, Tail(output), Tail(error),
                "A successful process exit is not sufficient evidence of correct behavior or performance improvement.");
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            return new ExecutionResult(id, "timed_out_or_cancelled", null, "", "",
                "Execution stopped by cancellation or sandbox time budget.");
        }
    }

    private static string Tail(string s) => s.Length > 16000 ? s[^16000..] : s;
}
