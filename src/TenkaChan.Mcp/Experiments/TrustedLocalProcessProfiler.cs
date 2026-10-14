using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Experiments;

public sealed record TrustedProcessSample(
    int Sequence, string Variant, bool IsWarmup, double WallMilliseconds,
    double CpuMilliseconds, long PeakWorkingSetBytes,
    int ExitCode, string StdoutSha256, string StderrTail);

public sealed record TrustedProcessComparison(
    string Status, double BaselineWallMedianMs, double CandidateWallMedianMs,
    double ChangePercent, double Lower95Percent, double Upper95Percent,
    string ComparisonBasis);

public sealed record TrustedProcessProfile(
    string Id, string Status, string BaselineAssembly, string BaselineSha256,
    string? CandidateAssembly, string? CandidateSha256,
    string TargetFramework, string EnvironmentFingerprint, string[] Arguments,
    int IterationsPerVariant, int WarmupsPerVariant,
    string StartedUtc, string FinishedUtc,
    IReadOnlyList<TrustedProcessSample> Samples,
    TrustedProcessComparison? Comparison,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Explicitly trusted, previously compiled .NET executable workload only.
/// Measures whole-process wall/CPU time on the actual host, NEVER attributes
/// process-launch or JIT cost to a particular C# method.
/// </summary>
public sealed class TrustedLocalProcessProfiler
{
    private readonly WorkspaceGuard _guard;
    private readonly bool _enabled;
    private readonly string _dataDir;

    public TrustedLocalProcessProfiler(WorkspaceGuard guard)
        : this(guard, Environment.GetEnvironmentVariable("TENKA_ENABLE_LOCAL_EXEC") == "1") { }

    public TrustedLocalProcessProfiler(WorkspaceGuard guard, bool enabled)
    {
        _guard = guard;
        _enabled = enabled;
        _dataDir = Path.Combine(
            Path.GetFullPath(Environment.GetEnvironmentVariable("TENKA_DATA_DIR")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "tenka-chan")), "trusted-local-process");
    }

    public async Task<TrustedProcessProfile> MeasureAsync(
        string baselineAssembly, bool trustedBinary,
        string? candidateAssembly = null, string[]? arguments = null,
        int repetitions = 5, int warmups = 1, int timeoutSeconds = 15,
        CancellationToken cancellationToken = default)
    {
        if (!_enabled)
            throw new UnauthorizedAccessException(
                "Host workload execution disabled: set TENKA_ENABLE_LOCAL_EXEC=1.");
        if (!trustedBinary)
            throw new UnauthorizedAccessException(
                "Each host workload run requires explicit trustedBinary=true.");
        if (repetitions is < 5 or > 10 ||
            warmups is < 0 or > 2 ||
            timeoutSeconds is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(repetitions),
                "Use 5–10 repetitions, 0–2 warmups and a 1–30 second per-launch budget.");
        var args = arguments ?? [];
        if (args.Length > 16 ||
            args.Any(x => x is null || x.Length > 1024 ||
                x.Any(c => c is '\0' or '\n' or '\r')))
            throw new ArgumentException("At most 16 bounded single-line process arguments.");

        var baseline = Validate(baselineAssembly);
        var candidate = candidateAssembly is null ? null : Validate(candidateAssembly);
        if (candidate is not null && candidate.Framework != baseline.Framework)
            throw new ArgumentException(
                "Both workloads must target the same declared runtime TFM; cross-version codegen comparison belongs in the JIT tools.");

        var started = DateTimeOffset.UtcNow.ToString("O");
        var id = Guid.NewGuid().ToString("N");
        var fingerprint = string.Join("|", RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture, RuntimeInformation.ProcessArchitecture,
            Environment.ProcessorCount, baseline.Framework);
        var observations = new List<TrustedProcessSample>();
        var sequence = 0;
        var samplesPerVariant = repetitions + warmups;
        var variants = candidate is null ? 1 : 2;
        var status = "completed";
        // Alternate ABBA by repetition to limit systematic order effects.
        // This is still not a process-affinity/steady-state microbenchmark.
        for (var iteration = 0; iteration < samplesPerVariant; iteration++)
        {
            var order = variants == 1 ? new[] { (Name: "baseline", App: baseline) }
                : iteration % 2 == 0
                    ? new[] { (Name: "baseline", App: baseline),
                        (Name: "candidate", App: candidate!) }
                    : new[] { (Name: "candidate", App: candidate!),
                        (Name: "baseline", App: baseline) };
            foreach (var (name, app) in order)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sample = await RunAsync(app, args, name, ++sequence,
                    iteration < warmups, timeoutSeconds, cancellationToken);
                observations.Add(sample);
                if (sample.ExitCode != 0)
                {
                    status = "workload_failed";
                    break;
                }
            }
            if (status != "completed") break;
        }

        var sourceHashAfter = Sha256File(baseline.Path);
        var candidateHashAfter = candidate is null ? null : Sha256File(candidate.Path);
        if (sourceHashAfter != baseline.Hash ||
            candidate is not null && candidateHashAfter != candidate.Hash)
            status = "binary_changed_during_measurement";

        var measured = observations.Where(x => !x.IsWarmup).ToArray();
        var outputs = measured.Select(x => x.StdoutSha256)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (status == "completed" && outputs.Length > 1)
            status = "non_equivalent_or_nondeterministic_output";

        if (status == "completed" && candidate is not null &&
            candidate.Hash == baseline.Hash)
            status = "identical_binaries_no_revision_comparison";

        TrustedProcessComparison? comparison = null;
        if (status == "completed" && candidate is not null)
        {
            var old = measured.Where(x => x.Variant == "baseline")
                .Select(x => x.WallMilliseconds).ToArray();
            var next = measured.Where(x => x.Variant == "candidate")
                .Select(x => x.WallMilliseconds).ToArray();
            if (old.Length == repetitions && next.Length == repetitions &&
                old.All(x => x > 0) && next.All(x => x > 0))
            {
                var result = PerformanceStatistics.Compare(old, next,
                    fingerprint, fingerprint, "ms/process", thresholdPercent: 5);
                comparison = new TrustedProcessComparison(result.Status,
                    result.BaselineMedian, result.CandidateMedian,
                    result.PercentChange, result.Lower95Percent,
                    result.Upper95Percent,
                    "Exploratory median/bootstrapped entire-process wall-time comparison; includes process startup, JIT, application work and shutdown.");
            }
        }

        var report = new TrustedProcessProfile(id, status,
            baseline.Path, baseline.Hash, candidate?.Path, candidate?.Hash,
            baseline.Framework, fingerprint, args.ToArray(),
            repetitions, warmups, started, DateTimeOffset.UtcNow.ToString("O"),
            observations, comparison,
            [
                "UNSANDBOXED: this tool executes previously compiled, explicitly trusted applications with the user's OS privileges.",
                "WallMilliseconds and CpuMilliseconds measure the entire child process, not individual methods. Process start, JIT compilation and shutdown are included.",
                "PeakWorkingSetBytes is process resident working set, not .NET managed allocations or GC heap size.",
                "Even alternating runs and bootstrap uncertainty cannot establish a causal optimization without controlled representative workloads.",
                "Output equality is assessed using SHA-256 of raw stdout bytes, not arbitrary application state or file side effects.",
                "Comparisons between identical DLL hashes are suppressed; the same binary cannot establish a revision speedup.",
                "Use a sampled CPU profile to locate expensive methods, and BenchmarkDotNet for steady-state microbenchmarks."
            ]);
        Save(report);
        return report;
    }

    public TrustedProcessProfile Get(string id)
    {
        if (!Guid.TryParseExact(id, "N", out _))
            throw new ArgumentException("Invalid local process profile ID.");
        var file = Path.Combine(_dataDir, id + ".json");
        if (!File.Exists(file)) throw new FileNotFoundException("Local profile not found.", file);
        if (new FileInfo(file).Length > 1_000_000)
            throw new InvalidDataException("Stored profile exceeds maximum size.");
        return JsonSerializer.Deserialize<TrustedProcessProfile>(File.ReadAllText(file))
            ?? throw new InvalidDataException("Malformed trusted process profile.");
    }

    private sealed record AppInfo(string Path, string Framework, string Hash);

    private AppInfo Validate(string path)
    {
        var dll = _guard.RequireFile(path, ".dll");
        var runtimeConfig = _guard.RequireFile(
            Path.ChangeExtension(dll, ".runtimeconfig.json"), ".json");
        if (new FileInfo(runtimeConfig).Length > 100_000)
            throw new InvalidDataException("runtimeconfig.json exceeds 100 KB.");
        using var file = File.OpenRead(runtimeConfig);
        using var json = JsonDocument.Parse(file);
        if (!json.RootElement.TryGetProperty("runtimeOptions", out var options)
            || !options.TryGetProperty("tfm", out var tfm)
            || tfm.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Expected a framework-dependent runnable net8.0+ application.");
        var selection = TargetFrameworkSelection.FromValidated(tfm.GetString()!, "runtimeconfig");
        return new AppInfo(dll, selection.TargetFramework, Sha256File(dll));
    }

    private static async Task<TrustedProcessSample> RunAsync(
        AppInfo app, string[] args, string variant, int sequence,
        bool warmup, int timeout, CancellationToken ct)
    {
        var config = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(app.Path)!,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        config.Environment.Clear();
        foreach (var key in new[]
        {
            "PATH", "DOTNET_ROOT", "DOTNET_ROOT(x86)", "HOME", "USERPROFILE",
            "SystemRoot", "WINDIR", "TEMP", "TMP", "TMPDIR",
            "ProgramFiles", "ProgramFiles(x86)", "PROCESSOR_ARCHITECTURE"
        })
        {
            var value = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(value)) config.Environment[key] = value;
        }
        config.Environment["DOTNET_NOLOGO"] = "1";
        config.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        config.ArgumentList.Add(app.Path);
        foreach (var arg in args) config.ArgumentList.Add(arg);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeout));
        using var process = new Process { StartInfo = config };
        if (!process.Start())
            throw new InvalidOperationException("Unable to launch trusted local workload.");
        var elapsed = Stopwatch.StartNew();
        var output = HashOutputAsync(process.StandardOutput.BaseStream);
        var error = TailAsync(process.StandardError);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            elapsed.Stop();
        }
        // A descendant can inherit stdout/stderr after the main process exits.
        // Do not let that hold the profiling request open indefinitely.
        await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(5));
        var outputHash = await output;
        var errorTail = await error;
        if (ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();
        var exit = timedOut ? -2 : process.ExitCode;
        return new TrustedProcessSample(sequence, variant, warmup,
            Math.Round(elapsed.Elapsed.TotalMilliseconds, 4),
            Math.Round(process.TotalProcessorTime.TotalMilliseconds, 4),
            process.PeakWorkingSet64, exit, outputHash,
            errorTail);
    }

    private static async Task<string> HashOutputAsync(Stream stream)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
            hash.AppendData(buffer.AsSpan(0, read));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static async Task<string> TailAsync(StreamReader stream)
    {
        var tail = new StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            tail.Append(buffer, 0, read);
            if (tail.Length > 2000) tail.Remove(0, tail.Length - 2000);
        }
        return tail.ToString();
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private void Save(TrustedProcessProfile result)
    {
        Directory.CreateDirectory(_dataDir);
        var file = Path.Combine(_dataDir, result.Id + ".json");
        var pending = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(pending, JsonSerializer.Serialize(result));
            File.Move(pending, file, overwrite: false);
        }
        finally { if (File.Exists(pending)) File.Delete(pending); }
    }
}
