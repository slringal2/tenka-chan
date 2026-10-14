using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Experiments;

public sealed record WorkspaceBuildResult(
    string Status, string ProjectPath, string? TargetFramework, bool RestoreRequested,
    int? ExitCode, string StartedAt, string FinishedAt, string Output, string Stderr,
    bool OutputTruncated, string Note);

public sealed record WorkspaceBuildProcessResult(int? ExitCode, string Output, string Stderr,
    bool TimedOut = false, bool Cancelled = false, bool OutputTruncated = false);

/// <summary>Injectable only for local regression tests; never selected by an HTTP request.</summary>
public interface IWorkspaceBuildProcess
{
    Task<WorkspaceBuildProcessResult> RunAsync(ProcessStartInfo startInfo, int timeoutSeconds,
        CancellationToken ct);
}

/// <summary>
/// Explicitly authorized host MSBuild execution, NOT a sandbox. No target app,
/// test runner, profiler, LLM or generated experiment is launched by this service.
/// Custom build targets and generators themselves can execute arbitrary code.
/// </summary>
public sealed class TrustedWorkspaceBuilder(
    WorkspaceGuard guard, IWorkspaceBuildProcess? process = null)
{
    private static readonly SemaphoreSlim BuildGate = new(1, 1);
    private readonly IWorkspaceBuildProcess _process = process ?? new DotnetWorkspaceBuildProcess();
    public bool Enabled => guard.TrustMsBuild &&
        Environment.GetEnvironmentVariable("TENKA_ENABLE_LOCAL_EXEC") == "1";

    public string Validate(string projectPath, string? framework, bool approved, int timeoutSeconds)
    {
        if (!Enabled || !approved)
            throw new UnauthorizedAccessException(
                "Building executes trusted MSBuild targets without isolation. TENKA_TRUST_MSBUILD=1, " +
                "TENKA_ENABLE_LOCAL_EXEC=1 and per-run approval are required.");
        if (timeoutSeconds is < 10 or > 300)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), "Use a 10-300 second build timeout.");
        ValidateFramework(framework);
        return guard.RequireFile(projectPath, ".csproj");
    }

    public async Task<WorkspaceBuildResult> BuildAsync(string projectPath, string? framework,
        bool allowRestore, bool approved, int timeoutSeconds = 180, CancellationToken ct = default)
    {
        var project = Validate(projectPath, framework, approved, timeoutSeconds);
        if (!await BuildGate.WaitAsync(0, ct))
            throw new InvalidOperationException("Another trusted workspace build is running.");
        try
        {
            var start = DateTimeOffset.UtcNow.ToString("O");
            WorkspaceBuildProcessResult result;
            string status;
            try
            {
                var info = CreateStartInfo(project, framework, allowRestore);
                result = await _process.RunAsync(info, timeoutSeconds, ct);
                status = result.Cancelled ? "cancelled" : result.TimedOut ? "timed_out"
                    : result.ExitCode == 0 ? "build_succeeded" : "build_failed";
            }
            catch (Win32Exception ex)
            {
                result = new(null, "", ex.Message);
                status = "dotnet_unavailable";
            }
            catch (OperationCanceledException)
            {
                result = new(null, "", "Build request cancelled.", Cancelled: true);
                status = "cancelled";
            }
            catch (IOException ex)
            {
                result = new(null, "", ex.Message);
                status = "build_io_error";
            }
            return new WorkspaceBuildResult(status, project,
                string.IsNullOrWhiteSpace(framework) ? null : framework, allowRestore,
                result.ExitCode, start, DateTimeOffset.UtcNow.ToString("O"),
                result.Output, result.Stderr, result.OutputTruncated,
                "UNSANDBOXED Release build with current user privileges. Build targets, generators and " +
                "package tasks may execute code or access files/network. --no-restore only disables implicit " +
                "NuGet restore; it is NOT network isolation. Child-process termination is best effort. " +
                "A successful build is not a test pass or proof that an output matches the opened source.");
        }
        finally { BuildGate.Release(); }
    }

    public static ProcessStartInfo CreateStartInfo(string project, string? framework, bool allowRestore)
    {
        ValidateFramework(framework);
        if (!Path.IsPathFullyQualified(project))
            throw new ArgumentException("An absolute project path is required.", nameof(project));
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(project)!,
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in new[] { "build", project, "--configuration", "Release",
            "--nologo", "--verbosity", "minimal", "--disable-build-servers", "-maxcpucount:1",
            "-nodeReuse:false", "-property:UseSharedCompilation=false" })
            info.ArgumentList.Add(argument);
        if (!allowRestore) info.ArgumentList.Add("--no-restore");
        if (!string.IsNullOrWhiteSpace(framework))
        {
            info.ArgumentList.Add("--framework");
            info.ArgumentList.Add(framework);
        }
        // Do not forward ambient model/cloud tokens, profiling hooks or MSBuild override variables.
        info.Environment.Clear();
        foreach (var key in new[] { "PATH", "SystemRoot", "WINDIR", "COMSPEC", "HOME", "USERPROFILE",
            "LOCALAPPDATA", "APPDATA", "TEMP", "TMP", "TMPDIR", "DOTNET_ROOT", "DOTNET_ROOT_X64",
            "ProgramFiles", "ProgramFiles(x86)" })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrEmpty(value)) info.Environment[key] = value;
        }
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1";
        var cache = Environment.GetEnvironmentVariable("TENKA_NUGET_PACKAGES");
        if (!string.IsNullOrWhiteSpace(cache)) info.Environment["NUGET_PACKAGES"] = Path.GetFullPath(cache);
        return info;
    }

    private static void ValidateFramework(string? framework)
    {
        if (string.IsNullOrWhiteSpace(framework)) return;
        if (framework.Length > 80 || !Regex.IsMatch(framework,
            @"^net(?:[89]|[1-9][0-9]+)\.[0-9]+(?:-[a-z0-9][a-z0-9.-]*)?$",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            throw new ArgumentException("Select a .NET 8+ target framework, or leave it blank for the project default.");
    }
}

/// <summary>One deadline covers process exit AND pipe draining, including inherited child pipes.</summary>
public sealed class DotnetWorkspaceBuildProcess : IWorkspaceBuildProcess
{
    public async Task<WorkspaceBuildProcessResult> RunAsync(ProcessStartInfo startInfo, int timeoutSeconds,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new IOException("The installed dotnet CLI could not be started.");
        var stdout = new BoundedLog();
        var stderr = new BoundedLog();
        var drains = Task.WhenAll(Drain(process.StandardOutput, stdout, deadline.Token),
            Drain(process.StandardError, stderr, deadline.Token));
        bool cancelled = false, timedOut = false;
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            await drains.WaitAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            cancelled = ct.IsCancellationRequested;
            timedOut = !cancelled;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            { stderr.Append("\nProcess-tree termination was not confirmed: " + ex.Message); }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
            { stderr.Append("\nThe process did not confirm exit within the cleanup window."); }
        }
        finally
        {
            // Always stop draining; a descendant can retain stdout after its parent exits.
            deadline.Cancel();
            try { await drains.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (TimeoutException) { stderr.Append("\nOutput drain was incomplete."); }
        }
        return new WorkspaceBuildProcessResult(process.HasExited ? process.ExitCode : null,
            stdout.Text, stderr.Text, timedOut, cancelled, stdout.Truncated || stderr.Truncated);
    }

    private static async Task Drain(StreamReader reader, BoundedLog output, CancellationToken ct)
    {
        var buffer = new char[2048];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
                output.Append(new string(buffer, 0, count));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        { output.Append("\nOutput stream closed: " + ex.Message); }
    }

    private sealed class BoundedLog
    {
        private readonly StringBuilder _buffer = new();
        private readonly object _sync = new();
        public bool Truncated { get; private set; }
        public string Text { get { lock (_sync) return _buffer.ToString(); } }
        public void Append(string value)
        {
            lock (_sync)
            {
                _buffer.Append(value);
                if (_buffer.Length > 32000)
                {
                    _buffer.Remove(0, _buffer.Length - 32000);
                    Truncated = true;
                }
            }
        }
    }
}
