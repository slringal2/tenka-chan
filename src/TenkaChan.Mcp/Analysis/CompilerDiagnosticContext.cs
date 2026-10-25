namespace TenkaChan.Mcp.Analysis;

/// <summary>
/// A diagnostic code breakdown from an in-memory Roslyn compilation, NOT a
/// verdict on the project's actual MSBuild output. The candidate classification
/// only suggests which missing references/partial solution should be examined.
/// </summary>
public sealed record CompilerDiagnosticCodeCount(string Id, int Count, bool MayBeReferenceResolutionRelated);

public static class CompilerDiagnosticContext
{
    private static readonly HashSet<string> ReferenceCodes = new(StringComparer.Ordinal)
    {
        "CS0006", // metadata file missing
        "CS0012", // referenced assembly missing
        "CS0234", // name absent in namespace
        "CS0246", // type/namespace missing
        "CS0400", // global namespace lookup failed
        "CS0518", // predefined type missing
        "CS1069"  // forwarded type missing assembly
    };
    public const string Scope = "loaded_roslyn_project_graph__not_a_verified_build";
    public const string Interpretation =
        "CompilerErrors are observed from the currently LOADED Roslyn project graph only. " +
        "A partial .slnx, unresolved project/package references, generated source not built yet, " +
        "or missing MSBuild targets may create diagnostics even when the full project builds. " +
        "Reference-related codes are HEURISTIC investigation candidates, not proven reference defects. " +
        "Do not label these errors confirmed code bugs until an explicitly authorized full build confirms them.";

    public static bool MayBeReferenceResolutionRelated(string diagnosticId) =>
        ReferenceCodes.Contains(diagnosticId);

    public static IReadOnlyList<CompilerDiagnosticCodeCount> TopErrorCodes(
        IReadOnlyDictionary<string, int> counts, int limit = 15)
    {
        ArgumentNullException.ThrowIfNull(counts);
        return counts.Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, 30))
            .Select(kv => new CompilerDiagnosticCodeCount(kv.Key, kv.Value,
                MayBeReferenceResolutionRelated(kv.Key))).ToArray();
    }
    public static int PotentialReferenceResolutionErrors(IReadOnlyDictionary<string, int> counts)
        => counts.Where(kv => MayBeReferenceResolutionRelated(kv.Key))
            .Sum(kv => kv.Value);
}
