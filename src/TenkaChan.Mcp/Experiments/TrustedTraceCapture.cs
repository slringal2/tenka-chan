using System.ComponentModel;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Experiments;

public sealed record TraceFileIdentity(string Name, string Sha256, long Length, string? Mvid = null);
public sealed record TraceProfileChoice(int Index, string Name, string Type, string Unit, int Observations);
public sealed record TraceCaptureReport(
    string Id, string Status, string ReportPath, string AssemblyPath, string TargetFramework,
    string MeasurementKind, string StartedUtc, string CompletedUtc,
    string ToolPath, string ToolVersion, int? CollectorExitCode, int? ConverterExitCode,
    IReadOnlyList<string> Arguments, IReadOnlyList<TraceFileIdentity> Inputs,
    TraceFileIdentity? NetTrace, TraceFileIdentity? Speedscope,
    IReadOnlyList<TraceProfileChoice> Profiles, string Output, IReadOnlyList<string> Limitations)
{
    // Null identifies older manifests, not a declaration that events were disabled.
    public string? RuntimeProviderConfiguration { get; init; }
}
public sealed record TraceBinaryComparison(string Status, bool SameRecordedInputs,
    IReadOnlyList<TraceFileIdentity> Recorded, IReadOnlyList<TraceFileIdentity> Current,
    string Limitation);

/// <summary>
/// Opt-in, UNSANDBOXED collection of an already-built trusted .NET 8+ app.
/// No build, package install, attach-to-arbitrary-PID or shell execution occurs.
/// Ordinary EventPipe stacks measure sampled thread time, NOT per-method CPU time.
/// </summary>
public sealed class TrustedTraceCapture
{
    internal const string Providers = "Microsoft-DotNETCore-SampleProfiler:0xF00000000000:4,Microsoft-Windows-DotNETRuntime:0x14C14FCCBD:4";
    internal const string RuntimeCostProviders = "Microsoft-DotNETCore-SampleProfiler:0xF00000000000:4,Microsoft-Windows-DotNETRuntime:0x14C14FCCBD:5";
    internal const long MaxTraceBytes = 128_000_000;
    internal const long MaxProfileBytes = 25_000_000;
    private static readonly SemaphoreSlim CaptureGate = new(1, 1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly WorkspaceGuard _guard;
    private readonly bool _enabled;
    private readonly string? _toolPath;
    private readonly string? _hostPath;

    public TrustedTraceCapture(WorkspaceGuard guard) : this(guard,
        Environment.GetEnvironmentVariable("TENKA_ENABLE_LOCAL_EXEC") == "1",
        Environment.GetEnvironmentVariable("TENKA_DOTNET_TRACE_PATH"),
        Environment.GetEnvironmentVariable("TENKA_DOTNET_HOST_PATH")) { }

    public TrustedTraceCapture(WorkspaceGuard guard, bool enabled, string? toolPath, string? hostPath = null)
    { _guard = guard; _enabled = enabled; _toolPath = toolPath; _hostPath = hostPath; }

    public async Task<TraceCaptureReport> CaptureAsync(string assemblyPath,
        string outputDirectory, bool trustedBinary, int timeoutSeconds = 60,
        IReadOnlyList<string>? arguments = null, CancellationToken ct = default,
        bool runtimeCostEvents = false)
    {
        if (!_enabled || !trustedBinary)
            throw new UnauthorizedAccessException("Set TENKA_ENABLE_LOCAL_EXEC=1 and explicitly supply trustedBinary=true. This launches UNSANDBOXED code with your OS permissions.");
        ValidateArguments(arguments ?? [], timeoutSeconds);
        ct.ThrowIfCancellationRequested();
        var tool = RequireExecutable(_toolPath, "TENKA_DOTNET_TRACE_PATH");
        var host = ResolveHost();
        var assembly = _guard.RequireFile(assemblyPath, ".dll");
        var outputRoot = _guard.RequireDirectory(outputDirectory);
        var inputs = ReadInputs(assembly);
        var tfm = ReadFramework(assembly);
        if (!await CaptureGate.WaitAsync(0, ct))
            throw new InvalidOperationException("Another trusted trace capture is already running.");
        try
        {
            var id = Guid.NewGuid().ToString("N");
            var folder = Path.Combine(outputRoot, "tenka-trace-" + id);
            if (Directory.Exists(folder) || File.Exists(folder)) throw new IOException("Capture directory already exists.");
            Directory.CreateDirectory(folder);
            _guard.RequireDirectory(folder);
            var reportPath = Path.Combine(folder, "capture.json");
            var tracePath = Path.Combine(folder, "workload.nettrace");
            var profilePath = Path.Combine(folder, "workload.speedscope.json");
            var started = DateTimeOffset.UtcNow.ToString("O");
            var status = "capture_failed";
            var version = "unavailable";
            var output = new StringBuilder();
            int? collectorExit = null, converterExit = null;
            TraceFileIdentity? trace = null, profile = null;
            IReadOnlyList<TraceProfileChoice> profiles = [];
            var limits = new List<string>
            {
                "UNSANDBOXED: the application and the operator-configured profiling tool run with the current user's OS permissions.",
                "EventPipe sampled-thread-time includes waiting and blocking. These are not on-CPU percentages or per-method CPU times.",
                "Input hashes/MVID identify files observed before and after collection, not runtime-loaded module identities or a Roslyn/source build match. Dependencies, native libraries and runtime files are not fully inventoried.",
                "Profiler buffers may drop events. Converted Speedscope loses information; retain the original nettrace for independent analysis.",
                "Collection covers application startup through exit. Tiered JIT/PGO are not disabled. Profiling overhead changes the workload.",
                "Timeout and file-size checks are best-effort process-tree cleanup, not an OS sandbox or disk quota. Partial captures cannot be imported as completed measurements.",
                "Stored SHA-256 values detect accidental file changes, not a malicious operator replacing both files and their unsigned manifest. Application arguments and traces may contain sensitive data."
            };
            if (runtimeCostEvents)
                limits.Add("Verbose runtime-event collection was explicitly requested. Allocation ticks and additional runtime events increase overhead and trace size; tick counts are not total object counts.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                var v = await RunAsync(Command(tool, folder, ["--version"]), folder, deadline.Token);
                if (v.ExitCode != 0) throw new InvalidOperationException("dotnet-trace version probe failed: " + v.Output);
                version = v.Output.Trim();
                if (version.Length > 500) version = version[..500];
                var collect = await RunAsync(BuildCollectCommand(tool, host, assembly, folder, arguments ?? [], runtimeCostEvents), folder, deadline.Token);
                collectorExit = collect.ExitCode;
                output.AppendLine(collect.Output);
                if (collectorExit != 0) throw new InvalidOperationException("Collector exited nonzero; no completed profile is asserted.");
                trace = Fingerprint(_guard.RequireFile(tracePath, ".nettrace"), MaxTraceBytes);
                if (trace.Length == 0) throw new InvalidDataException("Collector produced an empty trace.");
                var convert = await RunAsync(Command(tool, folder,
                    ["convert", tracePath, "--format", "Speedscope", "--output", Path.Combine(folder, "workload")]), folder, deadline.Token);
                converterExit = convert.ExitCode;
                output.AppendLine(convert.Output);
                if (converterExit != 0) throw new InvalidOperationException("Speedscope conversion failed.");
                profile = Fingerprint(_guard.RequireFile(profilePath, ".json"), MaxProfileBytes);
                profiles = ReadProfileChoices(profilePath);
                if (!profiles.Any(p => p.Observations > 0)) throw new InvalidDataException("No observed stacks in the converted trace.");
                var after = ReadInputs(assembly);
                status = inputs.SequenceEqual(after) ? "trace_completed" : "inputs_changed_during_capture";
            }
            catch (OperationCanceledException)
            { status = ct.IsCancellationRequested ? "cancelled" : "timed_out"; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or JsonException or Win32Exception or UnauthorizedAccessException)
            { output.AppendLine(ex.Message); }
            var text = output.ToString();
            if (text.Length > 16000) text = text[^16000..];
            var report = new TraceCaptureReport(id, status, reportPath, assembly, tfm,
                "eventpipe_sampled_thread_time", started, DateTimeOffset.UtcNow.ToString("O"),
                tool, version, collectorExit, converterExit, (arguments ?? []).ToArray(),
                inputs, trace, profile, profiles, text, limits)
            { RuntimeProviderConfiguration = runtimeCostEvents ? RuntimeCostProviders : Providers };
            SaveNew(reportPath, report);
            return report;
        }
        finally { CaptureGate.Release(); }
    }

    /// <summary>Read-only; no execution opt-in is needed to inspect saved evidence.</summary>
    public TraceCaptureReport Read(string reportPath)
    {
        var path = _guard.RequireFile(reportPath, ".json");
        if (Path.GetFileName(path) != "capture.json" || new FileInfo(path).Length > 1_000_000)
            throw new InvalidDataException("Expected a bounded capture.json manifest.");
        var report = JsonSerializer.Deserialize<TraceCaptureReport>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Malformed capture manifest.");
        if (!Guid.TryParseExact(report.Id, "N", out _) ||
            Path.GetFileName(Path.GetDirectoryName(path)) != "tenka-trace-" + report.Id ||
            report.MeasurementKind != "eventpipe_sampled_thread_time")
            throw new InvalidDataException("Capture identity or measurement kind is invalid.");
        report = report with { ReportPath = path };
        if (report.Status == "trace_completed")
        {
            VerifyArtifact(report, report.NetTrace, "workload.nettrace", MaxTraceBytes);
            VerifyArtifact(report, report.Speedscope, "workload.speedscope.json", MaxProfileBytes);
        }
        return report;
    }

    public string RequireProfile(TraceCaptureReport report, int profileIndex)
    {
        if (report.Status != "trace_completed") throw new InvalidOperationException("Only a completed, unchanged capture may be imported.");
        var path = VerifyArtifact(report, report.Speedscope, "workload.speedscope.json", MaxProfileBytes);
        var choices = ReadProfileChoices(path);
        if (profileIndex < 0 || profileIndex >= choices.Count)
            throw new ArgumentOutOfRangeException(nameof(profileIndex), "Select an explicit thread/profile index from the capture.");
        return path;
    }

    public TraceBinaryComparison CompareBinary(string reportPath, string assemblyPath)
    {
        var saved = Read(reportPath);
        var current = ReadInputs(_guard.RequireFile(assemblyPath, ".dll"));
        var same = saved.Inputs.SequenceEqual(current);
        return new TraceBinaryComparison(same ? "same_recorded_inputs" : "different_recorded_inputs", same,
            saved.Inputs, current, "Only the entry DLL, MVID, runtimeconfig and optional deps file are compared. This does not verify source, all loaded dependencies, or production runtime equality.");
    }

    private string VerifyArtifact(TraceCaptureReport report, TraceFileIdentity? expected, string filename, long maximum)
    {
        if (expected is null || expected.Name != filename || expected.Sha256 is null || expected.Sha256.Length != 64)
            throw new InvalidDataException("Missing or invalid capture artifact identity.");
        var path = _guard.RequireFile(Path.Combine(Path.GetDirectoryName(report.ReportPath)!, filename), Path.GetExtension(filename));
        var actual = Fingerprint(path, maximum);
        if (actual != expected) throw new InvalidDataException("Capture artifact hash/length mismatch: " + filename);
        return path;
    }

    private IReadOnlyList<TraceFileIdentity> ReadInputs(string assembly)
    {
        var dll = Fingerprint(_guard.RequireFile(assembly, ".dll"), 256_000_000);
        using (var stream = File.OpenRead(assembly))
        using (var pe = new PEReader(stream))
        {
            if (!pe.HasMetadata) throw new InvalidDataException("Expected a managed executable DLL.");
            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly || pe.PEHeaders.CorHeader?.EntryPointTokenOrRelativeVirtualAddress is null or 0)
                throw new InvalidDataException("Expected an already-built runnable managed DLL, not a library.");
            dll = dll with { Mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid).ToString("D") };
        }
        var runtime = _guard.RequireFile(Path.ChangeExtension(assembly, ".runtimeconfig.json"), ".json");
        var deps = Path.ChangeExtension(assembly, ".deps.json");
        return [dll, Fingerprint(runtime, 1_000_000), File.Exists(deps)
            ? Fingerprint(_guard.RequireFile(deps, ".json"), 5_000_000)
            : new TraceFileIdentity(Path.GetFileName(deps), "absent", 0)];
    }

    private string ReadFramework(string assembly)
    {
        var path = _guard.RequireFile(Path.ChangeExtension(assembly, ".runtimeconfig.json"), ".json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        if (!json.RootElement.TryGetProperty("runtimeOptions", out var options) ||
            !options.TryGetProperty("tfm", out var tfm) || tfm.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Missing runtimeconfig target framework.");
        return TargetFrameworkSelection.FromValidated(tfm.GetString()!, "runtimeconfig").TargetFramework;
    }

    internal static void ValidateArguments(IReadOnlyList<string> args, int timeoutSeconds)
    {
        if (timeoutSeconds is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
        if (args.Count > 16 || args.Any(a => a is null || a.Length > 1024 || a.IndexOfAny(['\0', '\r', '\n']) >= 0))
            throw new ArgumentException("At most 16 single-line arguments of at most 1024 characters each.");
    }

    internal static ProcessStartInfo BuildCollectCommand(string tool, string host, string assembly, string folder,
        IReadOnlyList<string> args, bool runtimeCostEvents = false)
    {
        ValidateArguments(args, 60);
        return Command(tool, folder, new[] { "collect", "--buffersize", "32", "--providers", runtimeCostEvents ? RuntimeCostProviders : Providers,
            "--format", "NetTrace", "--output", Path.Combine(folder, "workload.nettrace"), "--", host, "exec", assembly }.Concat(args));
    }

    private static ProcessStartInfo Command(string tool, string folder, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(tool) { WorkingDirectory = folder, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment.Clear();
        foreach (var key in new[] { "PATH", "HOME", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "TEMP", "TMP", "TMPDIR",
            "SystemRoot", "WINDIR", "COMSPEC", "ProgramFiles", "ProgramFiles(x86)", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64" })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (value is not null) info.Environment[key] = value;
        }
        info.Environment["DOTNET_EnableDiagnostics"] = "1";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        return info;
    }

    private string ResolveHost()
    {
        if (!string.IsNullOrWhiteSpace(_hostPath)) return RequireExecutable(_hostPath, "TENKA_DOTNET_HOST_PATH");
        var exe = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (Path.IsPathFullyQualified(directory) && File.Exists(Path.Combine(directory, exe)))
                return Path.Combine(directory, exe);
        throw new FileNotFoundException("dotnet host not found; configure TENKA_DOTNET_HOST_PATH.");
    }

    private static string RequireExecutable(string? path, string setting)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new FileNotFoundException("Configure " + setting + " as an absolute path to an operator-installed trusted executable. No tools are installed automatically.");
        return Path.GetFullPath(path);
    }

    internal static TraceFileIdentity Fingerprint(string path, long maximum)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > maximum) throw new InvalidDataException("File exceeds capture size limit: " + Path.GetFileName(path));
        return new TraceFileIdentity(Path.GetFileName(path), Convert.ToHexString(SHA256.HashData(file)), file.Length);
    }

    internal static IReadOnlyList<TraceProfileChoice> ReadProfileChoices(string path)
    {
        if (new FileInfo(path).Length > MaxProfileBytes) throw new InvalidDataException("Speedscope file exceeds 25 MB.");
        using var json = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { MaxDepth = 160 });
        if (!json.RootElement.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array ||
            profiles.GetArrayLength() > 10000) throw new InvalidDataException("Missing or oversized Speedscope profile list.");
        var result = new List<TraceProfileChoice>();
        foreach (var p in profiles.EnumerateArray())
        {
            if (!p.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String || type.GetString() is not ("sampled" or "evented") ||
                !p.TryGetProperty("unit", out var unit) || unit.ValueKind != JsonValueKind.String ||
                !p.TryGetProperty(type.GetString() == "sampled" ? "samples" : "events", out var observations) ||
                observations.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Invalid Speedscope profile header.");
            var name = p.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "profile" : "profile";
            result.Add(new TraceProfileChoice(result.Count, name.Length > 300 ? name[..300] : name,
                type.GetString()!, unit.GetString()!, observations.GetArrayLength()));
        }
        return result;
    }

    private sealed record PhaseResult(int ExitCode, string Output);
    private static async Task<PhaseResult> RunAsync(ProcessStartInfo command, string folder, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = command };
        if (!process.Start()) throw new InvalidOperationException("Could not start dotnet-trace.");
        var stdout = DrainAsync(process.StandardOutput);
        var stderr = DrainAsync(process.StandardError);
        // Keep stdin open; closing it may stop collection on some tool versions.
        try
        {
            var exit = process.WaitForExitAsync(CancellationToken.None);
            while (!exit.IsCompleted)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var (name, maximum) in new[] { ("workload.nettrace", MaxTraceBytes), ("workload.speedscope.json", MaxProfileBytes) })
                {
                    var file = new FileInfo(Path.Combine(folder, name));
                    if (file.Exists && file.Length > maximum) throw new InvalidDataException("Capture exceeded its output file-size limit.");
                }
                await Task.WhenAny(exit, Task.Delay(100, ct));
            }
            await exit;
            var text = await stdout.WaitAsync(TimeSpan.FromSeconds(5)) + "\n" + await stderr.WaitAsync(TimeSpan.FromSeconds(5));
            return new PhaseResult(process.ExitCode, text);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (TimeoutException) { }
            }
        }
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var tail = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
        { tail.Append(buffer, 0, count); if (tail.Length > 8000) tail.Remove(0, tail.Length - 8000); }
        return tail.ToString();
    }

    private static void SaveNew(string path, TraceCaptureReport report)
    {
        var pending = Path.Combine(Path.GetDirectoryName(path)!, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(file, report, Json);
            File.Move(pending, path, overwrite: false);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
}
