namespace TenkaChan.Mcp.Analysis;

public sealed record NativeInstructionFamilyDelta(string Family, int BaselineCount, int CandidateCount, int Delta);
public sealed record NativeMethodComparison(string JitSignature, string Architecture,
    int? BaselineCodeBytes, int? CandidateCodeBytes, int? ByteDelta,
    int BaselineInstructionCount, int CandidateInstructionCount,
    IReadOnlyList<NativeInstructionFamilyDelta> Families, IReadOnlyList<string> Observations);
public sealed record NativeCodeComparison(string BaselineFramework, string CandidateFramework,
    string Status, IReadOnlyList<NativeMethodComparison> Methods, IReadOnlyList<string> Limitations)
{
    public IReadOnlyList<string> UnpairedMethods { get; init; } = [];
    public bool ComparisonTruncated { get; init; }
}

/// <summary>Descriptive code generation comparison; never a speed or regression verdict.</summary>
public static class NativeCodeComparator
{
    public static NativeCodeComparison Compare(NativeCodeReport baseline, NativeCodeReport candidate, int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 100);
        var results = new List<NativeMethodComparison>();
        var skipped = new List<string>();
        var truncated = false;
        var before = baseline.Methods.GroupBy(m => m.JitSignature, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var after = candidate.Methods.GroupBy(m => m.JitSignature, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        if (!baseline.Truncated && !candidate.Truncated)
        {
            foreach (var signature in before.Keys.Union(after.Keys, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal))
            {
                if (!before.TryGetValue(signature, out var originals) || !after.TryGetValue(signature, out var replacements))
                { skipped.Add(signature + ": missing counterpart"); continue; }
                if (originals.Length != 1 || replacements.Length != 1)
                { skipped.Add(signature + ": multiple JIT code versions"); continue; }
                var left = originals[0];
                var right = replacements[0];
                if (!left.OffsetsReliable || !right.OffsetsReliable)
                { skipped.Add(signature + ": incomplete/unverified instruction bytes"); continue; }
                if (left.Architecture == "unknown" || left.Architecture != right.Architecture)
                { skipped.Add(signature + ": architecture mismatch"); continue; }
                if (left.JitMode.Length == 0 || left.JitMode != right.JitMode)
                { skipped.Add(signature + ": missing or different compilation tier/mode"); continue; }
                if (results.Count >= limit) { truncated = true; continue; }
                var families = left.InstructionFamilies.Keys.Concat(right.InstructionFamilies.Keys)
                    .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal)
                    .Select(f => new NativeInstructionFamilyDelta(f,
                        left.InstructionFamilies.GetValueOrDefault(f), right.InstructionFamilies.GetValueOrDefault(f),
                        right.InstructionFamilies.GetValueOrDefault(f) - left.InstructionFamilies.GetValueOrDefault(f))).ToArray();
                var observations = new List<string>();
                if (right.CodeSizeBytes > left.CodeSizeBytes) observations.Add("Candidate native code is larger in bytes.");
                if (right.CodeSizeBytes < left.CodeSizeBytes) observations.Add("Candidate native code is smaller in bytes.");
                if (left.Instructions.Count != right.Instructions.Count) observations.Add("Number of listed native instructions changed.");
                if (families.Any(x => x.Family == "vector_candidate" && x.Delta != 0))
                    observations.Add("Potential packed-vector instruction count changed; SIMD speedup is not established.");
                if (families.Any(x => x.Family == "call" && x.Delta != 0))
                    observations.Add("Native call count changed; this alone does not establish inlining or speedup.");
                if (observations.Count == 0) observations.Add("Summarized counts agree; machine bytes and operand values can still differ.");
                results.Add(new NativeMethodComparison(signature, left.Architecture, left.CodeSizeBytes,
                    right.CodeSizeBytes, right.CodeSizeBytes - left.CodeSizeBytes, left.Instructions.Count,
                    right.Instructions.Count, families, observations));
            }
        }
        return new NativeCodeComparison(baseline.TargetFramework, candidate.TargetFramework,
            baseline.Truncated || candidate.Truncated ? "incomplete_codegen_evidence"
            : results.Count == 0 ? "no_comparable_methods"
            : skipped.Count > 0 || truncated ? "partial_codegen_comparison" : "codegen_compared", results,
            [
                "Instruction families and code size are descriptive evidence, NOT benchmark timing or a performance regression result.",
                "Only complete, byte-length-verified methods with identical signatures, architectures and JIT modes are paired. Missing/ambiguous methods are reported separately.",
                "Same architecture is not the same CPU/ISA policy. Runtime/PGO/ISA exposure and inputs can change code generation and must be considered before measurement claims.",
                "Use matched repeated BenchmarkDotNet workloads or representative runtime profiles for actual performance changes."
            ]) { UnpairedMethods = skipped, ComparisonTruncated = truncated };
    }
}
