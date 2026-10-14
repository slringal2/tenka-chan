using TenkaChan.Mcp.Analysis;

namespace TenkaChan.Mcp.Experiments;

/// <summary>
/// Read-only evidence workflow: prioritize imported workload samples, append
/// separate static risk evidence, then optionally inspect a specifically
/// selected captured JIT method and IL/PDB token from that same experiment.
/// </summary>
public sealed class PerformanceEvidenceService(
    RoslynReviewService roslyn,
    ExperimentStore experiments,
    JitBinarySourceService binaries,
    TrustedLocalJitRunner? localJit = null)
{
    public async Task<PerformanceTriageReport> TriageAsync(
        string? jitExperimentId = null, int maxObserved = 50,
        int maxStatic = 30, string? benchmarkExperimentId = null,
        string? localJitCaptureId = null,
        CancellationToken cancellationToken = default)
    {
        jitExperimentId = NormalizeId(jitExperimentId);
        localJitCaptureId = NormalizeId(localJitCaptureId);
        ValidateNativeSelection(jitExperimentId, localJitCaptureId);
        var snapshot = roslyn.CurrentSnapshotId;
        var staticReport = await roslyn.AnalyzePerformanceHotspotsAsync(
            maxMethods: 500, maxSites: 2000, ct: cancellationToken);
        var profile = roslyn.CurrentHotspotProfile;
        var native = LoadNative(jitExperimentId, localJitCaptureId);
        var correlated = native is null ? null : roslyn.CorrelateNativeCode(native);
        if (roslyn.CurrentSnapshotId != snapshot ||
            staticReport.SnapshotId != snapshot)
            throw new InvalidOperationException(
                "Workspace changed during evidence collection; reopen or retry.");
        var triage = PerformanceEvidenceJoiner.Build(snapshot, staticReport, profile, correlated,
            jitExperimentId, maxObserved, maxStatic);
        return triage with
        {
            Benchmarks = LoadBenchmarkEvidence(benchmarkExperimentId),
            LocalJitCaptureId = localJitCaptureId,
            Limitations = localJitCaptureId is null ? triage.Limitations :
                triage.Limitations.Concat([
                    "Trusted local JIT is a saved host-native capture, not proof that the profiler, workspace and captured DLL share one build. Method matches remain signature-only; the captured assembly SHA-256 is not checked against the Roslyn source snapshot."
                ]).ToArray()
        };
    }

    public async Task<PerformanceMethodDossier> InvestigateAsync(
        string methodId, string? jitExperimentId = null,
        string? assemblyFile = null, int? methodToken = null,
        string? sourceFilePath = null, int maxInstructions = 1500,
        string? localJitCaptureId = null,
        CancellationToken cancellationToken = default)
    {
        jitExperimentId = NormalizeId(jitExperimentId);
        localJitCaptureId = NormalizeId(localJitCaptureId);
        ValidateNativeSelection(jitExperimentId, localJitCaptureId);
        if (localJitCaptureId is not null && assemblyFile is not null)
            throw new ArgumentException("Local JIT captures have no captured MethodDef/PDB artifacts; select a Docker JIT experiment for IL source binding.");
        if ((assemblyFile is null) != (methodToken is null))
            throw new ArgumentException("Both captured assemblyFile and methodToken are required for IL evidence.");
        if (sourceFilePath is not null && assemblyFile is null)
            throw new ArgumentException("A source path may only be supplied with captured IL/PDB evidence.");
        if (assemblyFile is not null && jitExperimentId is null)
            throw new ArgumentException("A JIT experiment ID is required for captured binary inspection.");

        var triage = await TriageAsync(jitExperimentId,
            maxObserved: 200, maxStatic: 100,
            localJitCaptureId: localJitCaptureId, cancellationToken: cancellationToken);
        // Never choose a method by approximate name if the profiling match was
        // ambiguous. Only exact Roslyn method IDs from a ranked result qualify.
        var entry = triage.Observed.FirstOrDefault(x => x.MethodId == methodId)
            ?? triage.StaticOnly.FirstOrDefault(x => x.MethodId == methodId);
        if (entry is null)
            throw new KeyNotFoundException("Method is not in the bounded triage report.");

        var native = LoadNative(jitExperimentId, localJitCaptureId);
        var correlated = native is null ? null : roslyn.CorrelateNativeCode(native);
        var matches = correlated?.Methods.Where(x => x.RoslynMethodId == methodId).Take(2)
            .ToArray() ?? [];
        var selected = matches.Length == 1 ? matches[0] : null;
        var status = matches.Length switch
        {
            > 1 => "ambiguous_multiple_jit_bodies",
            1 => "jit_name_signature_correlated_not_build_verified",
            _ => "no_jit_method_match"
        };

        BinarySourceEvidence? il = null;
        if (assemblyFile is not null && methodToken.HasValue)
        {
            var methods = binaries.Methods(jitExperimentId!, assemblyFile, limit: 2000);
            var declared = methods.Methods.SingleOrDefault(m => m.MetadataToken == methodToken.Value)
                ?? throw new ArgumentException(
                    "Metadata token was not present in the selected captured DLL catalog. Increase the catalog limit or select a token returned by jit_binary_methods.");
            il = binaries.Inspect(jitExperimentId!, assemblyFile, methodToken.Value,
                sourceFilePath, Math.Clamp(maxInstructions, 1, 3000));

            // The matched PE method name/type must agree with the native code
            // body. Otherwise this is just a user-selected arbitrary MethodDef,
            // and MUST NOT be returned as the source for the observed hotspot.
            if (selected is null ||
                !string.Equals(declared.Name, selected.MethodName, StringComparison.Ordinal) ||
                !TypeNamesAgree(declared.Type, selected.DeclaringType) ||
                !MethodParametersAgree(declared.Signature, selected.JitSignature))
                throw new InvalidOperationException(
                    "Selected captured IL MethodDef is not the uniquely matched JIT method. No cross-layer source link is asserted.");

            var pdbVerified = il.PdbStatus is
                "verified_external_portable_pdb" or "verified_embedded_portable_pdb";
            var locallyVerified = pdbVerified && sourceFilePath is not null &&
                il.Instructions.Any(i => i.Source?.LocalSourceStatus == "checksum_verified");
            status = locallyVerified
                ? "captured_il_pdb_and_local_source_checksum_verified__native_method_signature_only"
                : pdbVerified
                    ? "captured_il_pdb_verified__source_checksum_not_verified__native_method_signature_only"
                    : "captured_il_without_verified_pdb__native_method_signature_only";
        }

        if (roslyn.CurrentSnapshotId != triage.SnapshotId)
            throw new InvalidOperationException("Workspace changed during method investigation.");
        var dossier = PerformanceEvidenceJoiner.Dossier(
            triage, methodId, selected, il, status);

        // A single MCP response should not emit 20,000 machine instructions.
        // Preserve the original capture, report truncation in the evidence.
        if (selected is not null && selected.Instructions.Count > 400)
        {
            dossier = dossier with
            {
                NativeCode = selected with
                {
                    Instructions = selected.Instructions.Take(400).ToArray(),
                    Signals = selected.Signals.Take(60).ToArray()
                },
                Limitations = dossier.Limitations.Concat(
                    ["Native listing display limited to first 400 instructions; saved JIT artifact remains intact."])
                    .ToArray()
            };
        }
        return dossier;
    }

    private StandaloneBenchmarkEvidence? LoadBenchmarkEvidence(string? experimentId)
    {
        if (experimentId is null) return null;
        var (manifest, _) = experiments.Read(experimentId);
        var report = experiments.ReadExecutionResult(experimentId);
        return SummarizeBenchmark(experimentId, manifest, report);
    }

    internal static StandaloneBenchmarkEvidence SummarizeBenchmark(
        string experimentId, ExperimentManifest manifest, ExecutionResult report)
    {
        if (manifest.Id != experimentId || report.ExperimentId != experimentId ||
            manifest.Kind != "benchmark" || report.Kind != "benchmark" ||
            report.Status is not ("benchmark_completed" or "benchmark_partial") ||
            report.Benchmarks.Count == 0)
            throw new InvalidOperationException(
                "Expected a completed or explicitly partial BenchmarkDotNet experiment.");
        var cases = report.Benchmarks.Take(100)
            .Select(x => new StandaloneBenchmarkCase(x.CaseId, x.SampleCount,
                x.MeanNanoseconds, x.MedianNanoseconds,
                x.AllocatedBytesPerOperation, x.EnvironmentFingerprint))
            .ToArray();
        return new StandaloneBenchmarkEvidence(experimentId,
            manifest.TargetFramework, report.Status, cases,
            [
                "These are independent BenchmarkDotNet observations. Benchmark case names do not uniquely map to sampled profiler frames or the same workload.",
                "Do not infer a performance regression without comparable baseline/candidate runs, environment validation and independent samples.",
                "Mean and median are time per benchmark operation, not total application CPU usage.",
                report.Benchmarks.Count > cases.Length
                    ? "Benchmark cases were truncated for the evidence report."
                    : "All available benchmark cases fit within the 100-case display limit."
            ]);
    }

    private static string? NormalizeId(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : id.Trim();

    internal static void ValidateNativeSelection(string? experimentId, string? localCaptureId)
    {
        if (!string.IsNullOrWhiteSpace(experimentId) &&
            !string.IsNullOrWhiteSpace(localCaptureId))
            throw new ArgumentException("Choose either a Docker JIT experiment or a trusted local JIT capture, not both.");
    }

    internal static NativeCodeReport RequireCompletedLocalCapture(LocalJitCaptureResult capture)
    {
        if (capture.Status != "jit_completed" || capture.ExitCode != 0 ||
            capture.NativeDisassembly is null || capture.NativeDisassembly.MethodCount < 1 ||
            capture.AssemblySha256.Length != 64 ||
            !capture.AssemblySha256.All(Uri.IsHexDigit))
            throw new InvalidOperationException(
                "A completed, unchanged trusted local JIT capture with native instructions and a DLL SHA-256 is required.");
        return capture.NativeDisassembly;
    }

    private NativeCodeReport? LoadNative(string? experimentId, string? localCaptureId)
    {
        ValidateNativeSelection(experimentId, localCaptureId);
        if (localCaptureId is not null)
        {
            if (localJit is null)
                throw new InvalidOperationException("Saved trusted-local JIT capture reader is not registered.");
            return RequireCompletedLocalCapture(localJit.Get(localCaptureId));
        }
        if (experimentId is null) return null;
        var (manifest, _) = experiments.Read(experimentId);
        var result = experiments.ReadExecutionResult(experimentId);
        if (manifest.Kind != "jit" || result.Kind != "jit" ||
            result.Status != "jit_completed" ||
            result.NativeDisassembly is null || result.ExperimentId != experimentId)
            throw new InvalidOperationException(
                "A completed JIT experiment with native disassembly is required.");
        return result.NativeDisassembly;
    }

    /// <summary>
    /// Verify the captured MethodDef's parameter list against the printed JIT
    /// method signature. Reject names containing unsupported formatting rather
    /// than silently choosing a different overload.
    /// </summary>
    internal static bool MethodParametersAgree(
        string capturedMetadataSignature, string jitSignature)
    {
        var ilStart = capturedMetadataSignature.IndexOf('(');
        var ilEnd = capturedMetadataSignature.LastIndexOf(')');
        var jitStart = jitSignature.IndexOf('(');
        var jitEnd = jitSignature.LastIndexOf(')');
        if (ilStart < 0 || ilEnd <= ilStart || jitStart < 0 || jitEnd <= jitStart)
            return false;
        if (ilEnd != capturedMetadataSignature.Length - 1)
            return false;
        var returnType = jitSignature[(jitEnd + 1)..].Trim();
        if (returnType.Length > 0 &&
            (!returnType.StartsWith(':') ||
             returnType.IndexOfAny([',', ';', '(', ')']) >= 0))
            return false;
        var il = NormalizeParameters(
            capturedMetadataSignature[(ilStart + 1)..ilEnd]);
        var jit = NormalizeParameters(jitSignature[(jitStart + 1)..jitEnd]);
        if (il.Length > 600 || jit.Length > 600) return false;
        return string.Equals(il, jit, StringComparison.Ordinal);
    }

    private static string NormalizeParameters(string input)
    {
        var compact = new string(input.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return compact
            .Replace("System.Int32", "int", StringComparison.Ordinal)
            .Replace("System.Int64", "long", StringComparison.Ordinal)
            .Replace("System.Int16", "short", StringComparison.Ordinal)
            .Replace("System.UInt32", "uint", StringComparison.Ordinal)
            .Replace("System.UInt64", "ulong", StringComparison.Ordinal)
            .Replace("System.Boolean", "bool", StringComparison.Ordinal)
            .Replace("System.Char", "char", StringComparison.Ordinal)
            .Replace("System.String", "string", StringComparison.Ordinal)
            .Replace("System.Single", "float", StringComparison.Ordinal)
            .Replace("System.Double", "double", StringComparison.Ordinal)
            .Replace("System.Object", "object", StringComparison.Ordinal);
    }

    private static bool TypeNamesAgree(string metadataType, string jitType) =>
        !string.IsNullOrWhiteSpace(jitType) &&
        (string.Equals(metadataType.Replace('+', '.'), jitType.Replace('+', '.'),
            StringComparison.Ordinal) ||
         // Binary catalogs can use generic metadata arity, not source syntax.
         string.Equals(metadataType.Replace('/', '.').Replace('+', '.'),
            jitType.Replace('+', '.'), StringComparison.Ordinal));
}
