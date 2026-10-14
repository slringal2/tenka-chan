using System.Security.Cryptography;
using System.Text;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Experiments;

public sealed record NativeAssemblySelection(string? ListingPath = null,
    string? JitExperimentId = null, string? LocalJitCaptureId = null, int MethodIndex = 0);
public sealed record AssemblyWorkbenchReport(string SourceKind, string SourceId,
    string? ListingSha256, string TargetFramework, string RuntimeIdentity,
    bool SourceReportTruncated, int AvailableMethods, int SelectedMethodIndex,
    NativeInstructionAnalysis Analysis);
public sealed record AssemblyCodeComparison(bool SameSelectionSignature,
    bool SameReportedEnvironment, bool BothDecoded, bool? SameCodeBytes,
    int? CodeSizeDelta, int? InstructionCountDelta, string[] AddedCpuFeatures,
    string[] RemovedCpuFeatures, AssemblyWorkbenchReport Baseline,
    AssemblyWorkbenchReport Candidate, string Verdict, string[] Limitations);

/// <summary>Read-only assembly inspection. Never builds, profiles, executes or patches code.</summary>
public sealed class AssemblyWorkbench(WorkspaceGuard guard,
    ExperimentStore experiments, TrustedLocalJitRunner local)
{
    public AssemblyWorkbenchReport Analyze(NativeAssemblySelection selection)
    {
        var paths = new[] { selection.ListingPath, selection.JitExperimentId, selection.LocalJitCaptureId };
        if (paths.Count(x => !string.IsNullOrWhiteSpace(x)) != 1)
            throw new ArgumentException("Choose exactly one listingPath, jitExperimentId or localJitCaptureId.");
        NativeCodeReport report;
        string kind, id;
        string? sha = null;
        if (!string.IsNullOrWhiteSpace(selection.ListingPath))
        {
            id = guard.RequireFile(selection.ListingPath, ".txt");
            var data = BoundedBinaryFile.Read(id, 8_500_000);
            sha = Convert.ToHexString(SHA256.HashData(data));
            report = NativeJitDisassemblyParser.Parse(new UTF8Encoding(false, true).GetString(data),
                "unknown", "imported_listing_environment_unverified", "explicit-file", id, 50, 20000);
            kind = "imported_listing";
        }
        else if (!string.IsNullOrWhiteSpace(selection.LocalJitCaptureId))
        {
            id = selection.LocalJitCaptureId;
            var capture = local.Get(id);
            if (capture.Id != id || capture.Status != "jit_completed" || capture.ExitCode != 0 ||
                capture.NativeDisassembly is null)
                throw new InvalidDataException("A completed saved local JIT capture is required.");
            report = capture.NativeDisassembly;
            kind = "saved_trusted_local_jit";
        }
        else
        {
            id = selection.JitExperimentId!;
            var (manifest, _) = experiments.Read(id);
            var saved = experiments.ReadExecutionResult(id);
            if (manifest.Id != id || manifest.Kind != "jit" || saved.ExperimentId != id ||
                saved.Kind != "jit" || saved.Status != "jit_completed" || saved.NativeDisassembly is null)
                throw new InvalidDataException("A completed saved Docker JIT experiment is required.");
            report = saved.NativeDisassembly;
            kind = "saved_docker_jit";
        }
        if (selection.MethodIndex < 0 || selection.MethodIndex >= report.Methods.Count)
            throw new ArgumentOutOfRangeException(nameof(selection.MethodIndex), "Select a method index present in this capture; bodies are not chosen by approximate names.");
        return new(kind, id, sha, report.TargetFramework, report.RuntimeImage, report.Truncated,
            report.Methods.Count, selection.MethodIndex,
            NativeInstructionAnalyzer.Analyze(report.Methods[selection.MethodIndex]));
    }

    public AssemblyCodeComparison Compare(NativeAssemblySelection baseline, NativeAssemblySelection candidate)
    {
        var left = Analyze(baseline);
        var right = Analyze(candidate);
        var signatures = left.Analysis.Signature == right.Analysis.Signature &&
            left.Analysis.Architecture == right.Analysis.Architecture;
        var environment = left.SourceKind != "imported_listing" && right.SourceKind != "imported_listing" &&
            left.TargetFramework == right.TargetFramework && left.RuntimeIdentity == right.RuntimeIdentity;
        var decoded = left.Analysis.BytesDecoded && right.Analysis.BytesDecoded;
        return new(signatures, environment, decoded,
            decoded ? left.Analysis.CodeSha256 == right.Analysis.CodeSha256 : null,
            decoded && signatures ? right.Analysis.CodeSizeBytes - left.Analysis.CodeSizeBytes : null,
            decoded && signatures ? right.Analysis.Instructions.Count - left.Analysis.Instructions.Count : null,
            right.Analysis.CpuFeatures.Except(left.Analysis.CpuFeatures).Order().ToArray(),
            left.Analysis.CpuFeatures.Except(right.Analysis.CpuFeatures).Order().ToArray(),
            left, right, "code_generation_comparison_only",
            ["Smaller code or fewer instructions does not establish a speedup, lower allocations or a regression.",
             "Reported environment equality is not equal CPU, runtime patch, tier/PGO, workload or build verification.",
             "Address-dependent bytes and relocations can differ without a semantic change. No equivalence or source-line mapping is inferred."]);
    }
}
