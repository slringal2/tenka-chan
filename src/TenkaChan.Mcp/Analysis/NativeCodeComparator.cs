namespace TenkaChan.Mcp.Analysis;

public sealed record NativeInstructionFamilyDelta(
    string Family, int BaselineCount, int CandidateCount, int Delta);

public sealed record NativeMethodComparison(
    string JitSignature, string Architecture,
    int? BaselineCodeBytes, int? CandidateCodeBytes, int? ByteDelta,
    int BaselineInstructionCount, int CandidateInstructionCount,
    IReadOnlyList<NativeInstructionFamilyDelta> Families,
    IReadOnlyList<string> Observations);

public sealed record NativeCodeComparison(
    string BaselineFramework, string CandidateFramework,
    string Status, IReadOnlyList<NativeMethodComparison> Methods,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Mechanical side-by-side code-generation comparison only. Code size and
/// instruction counts do not establish throughput, latency or memory usage.
/// </summary>
public static class NativeCodeComparator
{
    public static NativeCodeComparison Compare(NativeCodeReport baseline,
        NativeCodeReport candidate, int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 100);
        var results = new List<NativeMethodComparison>();
        var ambiguous = false;

        var before = baseline.Methods.GroupBy(m => m.JitSignature, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var after = candidate.Methods.GroupBy(m => m.JitSignature, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        foreach (var (signature, originals) in before.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            if (!after.TryGetValue(signature, out var replacements)) continue;
            if (originals.Length != 1 || replacements.Length != 1)
            {
                ambiguous = true;
                continue;
            }

            var left = originals[0];
            var right = replacements[0];
            if (left.Architecture == "unknown" ||
                left.Architecture != right.Architecture)
            {
                ambiguous = true;
                continue;
            }

            var families = left.InstructionFamilies.Keys
                .Concat(right.InstructionFamilies.Keys)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .Select(f => new NativeInstructionFamilyDelta(f,
                    left.InstructionFamilies.GetValueOrDefault(f),
                    right.InstructionFamilies.GetValueOrDefault(f),
                    right.InstructionFamilies.GetValueOrDefault(f) -
                    left.InstructionFamilies.GetValueOrDefault(f))).ToArray();

            var observations = new List<string>();
            if (left.CodeSizeBytes is int a && right.CodeSizeBytes is int b)
            {
                if (b > a) observations.Add("Candidate native code is larger in bytes.");
                if (b < a) observations.Add("Candidate native code is smaller in bytes.");
            }
            if (left.Instructions.Count != right.Instructions.Count)
                observations.Add("Number of decoded native instructions changed.");
            if (families.Any(x => x.Family == "vector_candidate" && x.Delta != 0))
                observations.Add("Count of potentially vector instructions changed; this is not proof of vectorized workload speedup.");
            if (families.Any(x => x.Family == "call" && x.Delta != 0))
                observations.Add("Native call count differs; this does not directly establish inlining.");
            if (observations.Count == 0)
                observations.Add("No differences detected in summarized metrics; machine bytes may still differ.");

            results.Add(new NativeMethodComparison(signature, left.Architecture,
                left.CodeSizeBytes, right.CodeSizeBytes,
                left.CodeSizeBytes is int oldBytes && right.CodeSizeBytes is int newBytes
                    ? newBytes - oldBytes : null,
                left.Instructions.Count, right.Instructions.Count, families, observations));
            if (results.Count >= limit) break;
        }

        return new NativeCodeComparison(baseline.TargetFramework,
            candidate.TargetFramework,
            results.Count == 0 ? "no_comparable_methods"
                : ambiguous ? "partial_codegen_comparison" : "codegen_compared",
            results,
            [
                "Code size, instruction counts and opcode families are descriptive evidence, NOT benchmark timing or a performance regression result.",
                "Different runtime versions, instruction-set exposure or JIT policies can affect assembly even with the same source.",
                "Only identically named JIT methods on the same architecture are compared; different signatures, missing methods and ambiguous duplicates remain unpaired.",
                "For a controlled speed claim, measure identical workloads with BenchmarkDotNet or a representative runtime profiler."
            ]);
    }
}
