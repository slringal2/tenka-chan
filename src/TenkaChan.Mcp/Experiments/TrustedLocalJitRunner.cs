using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Experiments;

public sealed record LocalJitCaptureResult(
    string Id, string Status, string AssemblyPath, string TargetFramework,
    string JitFilter, string AssemblySha256, string StartedUtc, string CompletedUtc,
    int? ExitCode, string Stdout, string Stderr, string NativeListingPath,
    NativeCodeReport? NativeDisassembly, IReadOnlyList<string> Limitations);

/// <summary>
/// Explicit opt-in, UNSANDBOXED execution of a previously compiled and trusted
/// managed application on its actual host OS/runtime. Unlike generated tests,
/// the target is NEVER compiled or synthesized by this tool. The application
/// has the same OS permissions as the user running Tenka: this is NOT a
/// substitute for Docker when reviewing AI-generated/untrusted code.
/// </summary>
public sealed class TrustedLocalJitRunner
{
    private readonly WorkspaceGuard _guard;
    private readonly bool _enabled;
    private readonly string _root;

    public TrustedLocalJitRunner(WorkspaceGuard guard)
        : this(guard, Environment.GetEnvironmentVariable("TENKA_ENABLE_LOCAL_EXEC") == "1")
    {
    }

    public TrustedLocalJitRunner(WorkspaceGuard guard, bool enabled)
    {
        _guard = guard;
        _enabled = enabled;
        _root = Path.Combine(
            Path.GetFullPath(Environment.GetEnvironmentVariable("TENKA_DATA_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "tenka-chan")),
            "trusted-local-jit");
    }

    public bool Enabled => _enabled;

    public async Task<LocalJitCaptureResult> CaptureAsync(
        string assemblyPath, string jitFilter,
        bool trustedBinary, int timeoutSeconds = 45,
        IReadOnlyList<string>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        if (!_enabled)
            throw new UnauthorizedAccessException(
                "Local execution is disabled. Set TENKA_ENABLE_LOCAL_EXEC=1 after reviewing the trust implications.");
        if (!trustedBinary)
            throw new UnauthorizedAccessException(
                "Explicit trustedBinary=true is required for each local execution.");
        var assembly = _guard.RequireFile(assemblyPath, ".dll");
        var filter = TargetFrameworkPolicy.ValidateJitFilter(jitFilter);
        if (timeoutSeconds is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds),
                "Local execution timeout must be between 1 and 120 seconds.");
        var args = arguments ?? [];
        if (args.Count > 16 || args.Any(a => a is null || a.Length > 1024 ||
            a.Any(c => c == '\0' || c == '\n' || c == '\r')))
            throw new ArgumentException("At most 16 bounded single-line arguments are supported.");

        // Requiring the runtimeconfig makes it clear that an executable,
        // framework-dependent .NET application was supplied, not a library
        // chosen at random and silently compiled by an AI agent.
        var runtimePath = _guard.RequireFile(
            Path.ChangeExtension(assembly, ".runtimeconfig.json"), ".json");
        var tfm = ReadRuntimeFramework(runtimePath);
        var selected = TargetFrameworkSelection.FromValidated(tfm, "runtimeconfig");
        var binaryHash = HashFile(assembly);
        var id = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        var listing = Path.Combine(dir, "jit-disassembly.txt");
        var started = DateTimeOffset.UtcNow.ToString("O");
        string stdoutText = "", stderrText = "";
        int? exitCode = null;
        string? interrupted = null;
        Task<string>? stdout = null, stderr = null;
        var launched = false;

        using var process = new Process { StartInfo = ConfigureProcess(assembly, filter, listing, args) };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            launched = process.Start();
            if (!launched) throw new InvalidOperationException("Could not start dotnet host.");
            stdout = TailAsync(process.StandardOutput, 12000);
            stderr = TailAsync(process.StandardError, 12000);
            await process.WaitForExitAsync(deadline.Token);
            exitCode = process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            interrupted = cancellationToken.IsCancellationRequested ? "cancelled" : "timed_out";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            interrupted = "dotnet_host_unavailable";
            stderrText = ex.Message;
        }
        finally
        {
            if (launched && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { }
            }
            if (stdout is not null) stdoutText = await stdout;
            if (stderr is not null)
            {
                var captured = await stderr;
                stderrText = stderrText.Length == 0 ? captured : stderrText + "\n" + captured;
            }
        }

        NativeCodeReport? native = null;
        var remarks = new List<string>
        {
            "UNSANDBOXED: the trusted application ran with the host user's filesystem/network privileges. Never use for generated or unknown code.",
            "Native instructions are code-generation facts, not elapsed CPU time, optimization gains, or allocated bytes.",
            "JIT mode forces ReadyToRun=0 and TieredCompilation=0. The native code may differ from production Tiered PGO.",
            "TargetFramework is read from runtimeconfig.json. .NET runtime roll-forward can select a different installed runtime; inspect the host runtime configuration before comparing code generation.",
            "Use a separately captured representative CPU profile and benchmarks to determine where execution is actually expensive."
        };
        var status = interrupted ?? (exitCode == 0 ? "completed_no_native_listing" : "target_exited_nonzero");
        if (File.Exists(listing))
        {
            try
            {
                native = NativeJitDisassemblyParser.Read(listing,
                    selected.TargetFramework, "local-host:" + Environment.OSVersion.Platform,
                    filter, maxMethods: 30, maxInstructions: 6000);
                if (interrupted is null && exitCode == 0)
                    status = native.InstructionCount > 0 && native.MethodCount > 0
                        ? "jit_completed" : "jit_no_matching_method";
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                remarks.Add("Native JIT listing could not be parsed: " + ex.Message);
                if (interrupted is null && exitCode == 0) status = "jit_report_invalid";
            }
        }
        var digestAfter = HashFile(assembly);
        if (!string.Equals(binaryHash, digestAfter, StringComparison.Ordinal))
        {
            remarks.Add("The target DLL changed during execution. The captured native report cannot be securely attributed to the original file.");
            native = null;
            status = "binary_modified_during_capture";
        }
        var result = new LocalJitCaptureResult(id, status, assembly,
            selected.TargetFramework, filter, binaryHash, started,
            DateTimeOffset.UtcNow.ToString("O"), exitCode,
            stdoutText, stderrText, listing, native, remarks);
        Save(result);
        return result;
    }

    public LocalJitCaptureResult Get(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new ArgumentException("Invalid local capture ID.");
        var path = Path.Combine(_root, id, "report.json");
        if (!File.Exists(path))
            throw new FileNotFoundException("No saved trusted-local JIT result.", path);
        if (new FileInfo(path).Length > 2_000_000)
            throw new InvalidDataException("Saved local JIT report is too large.");
        return JsonSerializer.Deserialize<LocalJitCaptureResult>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Saved local JIT report was malformed.");
    }

    private void Save(LocalJitCaptureResult result)
    {
        var folder = Path.Combine(_root, result.Id);
        Directory.CreateDirectory(folder);
        var report = Path.Combine(folder, "report.json");
        var pending = Path.Combine(folder, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            // Persistent JSON is intentionally bounded. The full text listing
            // is stored as a separate file in the same local app-data folder.
            var bounded = result with
            {
                NativeDisassembly = result.NativeDisassembly is null
                    ? null : result.NativeDisassembly with
                    {
                        Truncated = result.NativeDisassembly.Truncated ||
                            result.NativeDisassembly.Methods.Count > 15 ||
                            result.NativeDisassembly.Methods.Any(method =>
                                method.Instructions.Count > 500 || method.Signals.Count > 50),
                        Methods = result.NativeDisassembly.Methods.Take(15)
                            .Select(method => method with
                            {
                                Instructions = method.Instructions.Take(500).ToArray(),
                                Signals = method.Signals.Take(50).ToArray(),
                                ParseWarnings = method.Instructions.Count > 500 ||
                                    method.Signals.Count > 50
                                    ? method.ParseWarnings.Concat(
                                        ["Saved report view truncated; inspect the raw local JIT text for the complete listing."])
                                        .ToArray()
                                    : method.ParseWarnings
                            }).ToArray()
                    }
            };
            File.WriteAllText(pending, JsonSerializer.Serialize(bounded));
            File.Move(pending, report, overwrite: true);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }

    private static string ReadRuntimeFramework(string path)
    {
        if (new FileInfo(path).Length > 250_000)
            throw new InvalidDataException("Runtimeconfig JSON is too large.");
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        if (!doc.RootElement.TryGetProperty("runtimeOptions", out var opts) ||
            !opts.TryGetProperty("tfm", out var tfm) ||
            tfm.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("The runnable DLL must have a runtimeconfig.json with a literal net8.0+ tfm.");
        return tfm.GetString()!;
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static ProcessStartInfo ConfigureProcess(
        string dll, string filter, string report, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(dll)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        // Do not hand AI agents' credentials, TENKA_* controls or arbitrary
        // inherited environment variables to the executed application.
        start.Environment.Clear();
        foreach (var key in new[]
        {
            "PATH", "DOTNET_ROOT", "DOTNET_ROOT(x86)", "HOME", "USERPROFILE",
            "SystemRoot", "WINDIR", "TEMP", "TMP", "TMPDIR",
            "ProgramFiles", "ProgramFiles(x86)", "PROCESSOR_ARCHITECTURE"
        })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value)) start.Environment[key] = value;
        }
        start.Environment["DOTNET_JitDisasm"] = filter;
        start.Environment["DOTNET_JitDisasmWithCodeBytes"] = "1";
        start.Environment["DOTNET_JitStdOutFile"] = report;
        start.Environment["DOTNET_ReadyToRun"] = "0";
        start.Environment["DOTNET_TieredCompilation"] = "0";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.ArgumentList.Add(dll);
        foreach (var item in arguments) start.ArgumentList.Add(item);
        return start;
    }

    private static async Task<string> TailAsync(StreamReader reader, int characters)
    {
        var buffer = new char[4096];
        var tail = new StringBuilder(characters + 4096);
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            tail.Append(buffer, 0, read);
            if (tail.Length > characters)
                tail.Remove(0, tail.Length - characters);
        }
        return tail.ToString();
    }
}
