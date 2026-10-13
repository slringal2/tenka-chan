using TenkaChan.Mcp.Analysis;

namespace TenkaChan.Mcp.Experiments;

/// <summary>
/// Compare saved native JIT outputs only when the generated invocation
/// harness/selector are identical. Target project binaries may differ to
/// support an intentional before/after optimization or runtime comparison.
/// </summary>
public sealed class NativeExperimentComparer(ExperimentStore store)
{
    public NativeCodeComparison Compare(string baselineExperimentId,
        string candidateExperimentId)
    {
        if (baselineExperimentId == candidateExperimentId)
            throw new ArgumentException("Select two different experiment IDs.");
        var (original, _) = store.Read(baselineExperimentId);
        var (updated, _) = store.Read(candidateExperimentId);
        if (original.Kind != "jit" || updated.Kind != "jit")
            throw new ArgumentException("Both experiments must be JIT native-code runs.");
        if (original.JitFilter != updated.JitFilter ||
            original.SourceSha256 != updated.SourceSha256)
            throw new InvalidOperationException(
                "Native comparisons require identical method selectors and invocation harness source. " +
                "Change the application/project versions separately.");
        var baseline = store.ReadExecutionResult(baselineExperimentId);
        var candidate = store.ReadExecutionResult(candidateExperimentId);
        if (baseline.Status != "jit_completed" ||
            candidate.Status != "jit_completed" ||
            baseline.NativeDisassembly is null ||
            candidate.NativeDisassembly is null)
            throw new InvalidOperationException(
                "Both JIT runs must have completed with parseable native instructions.");
        return NativeCodeComparator.Compare(baseline.NativeDisassembly,
            candidate.NativeDisassembly);
    }
}
