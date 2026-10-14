using System.Security.Cryptography;
using System.Text;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Analysis;

public sealed record InvestigationProfileEvidence(string Name, string? Sha256, int ProfileIndex,
    string Unit, string ProfileType, string Association,
    ProfileMethodCost? MethodCost, double TotalWeight);
public sealed record InvestigationBuildEvidence(string ExperimentId, string TargetFramework,
    string AssemblyFile, BinarySourceEvidence IlAndSource,
    IReadOnlyList<NativeMethodDisassembly> NativeMethods,
    IReadOnlyList<string> Associations);
public sealed record MethodPerformanceInvestigation(string SnapshotId, string SourceTextSha256,
    MethodInspection Method, InvestigationProfileEvidence? Profile,
    HotspotMethodRanking? StaticRanking, IReadOnlyList<HotspotSite> StaticSites,
    bool StaticDetailsTruncated, InvestigationBuildEvidence? CompiledEvidence,
    IReadOnlyList<string> Limitations);

/// <summary>
/// A read-only evidence join. Does NOT execute benchmarks, traces or models.
/// The source/workload and compiled-artifact views have distinct provenance;
/// they are not promoted to one exact native/IL/source mapping.
/// </summary>
public sealed class PerformanceInvestigationService(RoslynReviewService roslyn,
    ExperimentStore experiments, WorkspaceGuard guard)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StaticHotspotReport? _screening;

    public async Task<PerformanceTriageReport> TriageAsync(string ranking = "self", int limit = 100,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (screening, profile) = await CaptureAsync(cancellationToken);
            var report = PerformanceTriageEngine.Build(screening, profile, ranking, limit);
            RequireUnchanged(screening.SnapshotId, profile);
            return report;
        }
        finally { _gate.Release(); }
    }

    public async Task<MethodPerformanceInvestigation> InvestigateAsync(string methodId,
        string? jitExperimentId = null, string? assemblyFile = null, int? methodToken = null,
        string? sourceFilePath = null, CancellationToken cancellationToken = default)
    {
        if ((jitExperimentId is null) != (assemblyFile is null) ||
            (jitExperimentId is null) != (methodToken is null))
            throw new ArgumentException("Provide jitExperimentId, assemblyFile and methodToken together, or omit all three.");
        if (sourceFilePath is not null && jitExperimentId is null)
            throw new ArgumentException("sourceFilePath requires a captured JIT binary selection.");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var (screening, profile) = await CaptureAsync(cancellationToken);
            var method = await roslyn.InspectAsync(methodId, includeCallers: false, cancellationToken);
            RequireUnchanged(screening.SnapshotId, profile);
            var observed = profile?.MethodCosts.SingleOrDefault(m => m.MethodId == methodId);
            InvestigationBuildEvidence? build = null;
            if (jitExperimentId is not null)
            {
                var binaries = new JitBinarySourceService(experiments, guard);
                // Paths come from the caller or allowlisted Roslyn workspace,
                // never from a PDB document URL or an untrusted profile label.
                var il = binaries.Inspect(jitExperimentId, assemblyFile!, methodToken!.Value,
                    sourceFilePath ?? method.Method.File, maxInstructions: 2000);
                RequireSameSimpleSignature(il.Signature, method.Method.Signature);
                var result = experiments.ReadExecutionResult(jitExperimentId);
                var native = result.NativeDisassembly;
                var correlated = native is null ? [] : roslyn.CorrelateNativeCode(native).Methods
                    .Where(m => m.RoslynMethodId == methodId).ToArray();
                build = new InvestigationBuildEvidence(jitExperimentId, result.TargetFramework,
                    assemblyFile!, il, correlated,
                    [
                        "The captured DLL/PDB are hash-checked against this experiment. IL source spans and optional current source checksums retain their own verification statuses.",
                        "The selected binary method and Roslyn declaration have matching simple signatures. This alone is NOT proof that the loaded workspace was compiled into this DLL.",
                        "Native methods are matched by the existing Roslyn signature correlator. No native-instruction to IL-offset mapping is asserted.",
                        "The workload profile is from a separately imported file. Its names do not establish that it measured this JIT experiment or binary build."
                    ]);
            }
            RequireUnchanged(screening.SnapshotId, profile);
            return new MethodPerformanceInvestigation(screening.SnapshotId,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(method.Source))), method,
                profile is null ? null : new InvestigationProfileEvidence(profile.ProfileName,
                    profile.SourceSha256, profile.ProfileIndex, profile.Unit, profile.ProfileType,
                    observed is null ? "not_observed_not_proven_cold" : "qualified_name_only_build_unverified",
                    observed, profile.TotalWeight),
                screening.Methods.SingleOrDefault(m => m.Method.Id == methodId),
                screening.Sites.Where(s => s.MethodId == methodId).Take(30).ToArray(),
                screening.MethodsTruncated || screening.SitesTruncated, build,
                [
                    "This dossier groups independent observations for investigation, not a verified causal explanation of slowness or a speedup verdict.",
                    "SourceTextSha256 identifies the loaded Roslyn method text, not the assembly build. Disk source may have changed since workspace loading.",
                    "Profile/IL/native code identities are intentionally not conflated. Read each association and source checksum status.",
                    "No automatic patch, test execution, benchmark or profiler launch occurs. Verify behavior and measure equivalent workloads separately."
                ]);
        }
        finally { _gate.Release(); }
    }

    private async Task<(StaticHotspotReport Screening, SampledHotspotReport? Profile)> CaptureAsync(CancellationToken ct)
    {
        string snapshot = roslyn.CurrentSnapshotId;
        var profile = roslyn.CurrentHotspotProfile;
        var screening = _screening;
        if (screening is null || screening.SnapshotId != snapshot)
            screening = await roslyn.AnalyzePerformanceHotspotsAsync(500, 2000, ct);
        RequireUnchanged(snapshot, profile);
        if (screening.SnapshotId != snapshot)
            throw new InvalidOperationException("Workspace changed while preparing investigation. Retry after loading finishes.");
        _screening = screening;
        return (screening, profile);
    }

    private void RequireUnchanged(string snapshot, SampledHotspotReport? profile)
    {
        if (roslyn.CurrentSnapshotId != snapshot || !ReferenceEquals(profile, roslyn.CurrentHotspotProfile))
            throw new InvalidOperationException("Workspace or imported profile changed during investigation. Retry using the current snapshot.");
    }

    internal static void RequireSameSimpleSignature(string binary, string roslynSignature)
    {
        var space = binary.IndexOf(' ');
        if (space < 0) throw new InvalidDataException("Unrecognized binary method signature.");
        var member = binary[(space + 1)..].Replace("::", ".", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal);
        var expected = roslynSignature.Replace(" ", "", StringComparison.Ordinal);
        // Exact simple-signature equality is deliberately conservative.
        // Generic metadata parameters, custom modifiers, ref syntax, nested
        // metadata names etc. require manual inspection through jit_source_inspect.
        if (!string.Equals(member, expected, StringComparison.Ordinal))
            throw new InvalidOperationException("Binary/Roslyn method signatures do not match, or use an unsupported complex signature. Use jit_source_inspect for manual inspection; no evidence was joined.");
    }
}
