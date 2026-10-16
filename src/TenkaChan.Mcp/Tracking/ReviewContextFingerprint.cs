using System.Text.Json;

namespace TenkaChan.Mcp.Tracking;

public sealed record ReviewContextComponent(string Name, string Hash);
public sealed record ReviewContextFingerprint(int Version, string Hash, bool Complete,
    IReadOnlyList<ReviewContextComponent> Components);
public sealed record ReviewContextChange(string Status, int? PreviousVersion, int CurrentVersion,
    IReadOnlyList<string> ChangedComponents, string Note);

/// <summary>
/// Versioned, bounded diagnostics for the conservative project/dependency context.
/// Component hashes explain a change; they never establish correctness or speedup.
/// Legacy checkpoints have no components, so mismatches cannot be silently rebased.
/// </summary>
public static class ReviewContextFingerprints
{
    public const int Version = 2;
    public static IReadOnlyList<string> ComponentNames { get; } = Array.AsReadOnly(new[]
    {
        "sourceFiles", "generatedSources", "projectFiles", "parseOptions",
        "compilationOptions", "metadataReferences", "projectReferences"
    });

    // Normalize BEFORE ordering. Keep duplicates; repeated references are not a set.
    public static string Digest(IEnumerable<string> values) => ReviewProgress.Hash(JsonSerializer.Serialize(
        values.Select(ReviewProgress.Normalize).Order(StringComparer.Ordinal).ToArray()));

    public static ReviewContextFingerprint Create(IReadOnlyDictionary<string, string> hashes, bool complete)
    {
        if (hashes.Count != ComponentNames.Count || ComponentNames.Any(name =>
            !hashes.TryGetValue(name, out var componentHash) || !IsHash(componentHash)))
            throw new ArgumentException("All review context component hashes are required.");
        var components = ComponentNames.Select(name => new ReviewContextComponent(name, hashes[name])).ToArray();
        var hash = Digest(components.Select(c => c.Name + "=" + c.Hash)
            .Append("schema=review-context-v2").Append("complete=" + (complete ? "1" : "0")));
        return new(Version, hash, complete, Array.AsReadOnly(components));
    }

    public static bool MatchesLegacy(ReviewTarget target, ReviewCheckpoint mark) =>
        mark.ContextFingerprint is null && target.ContextFingerprint is { Complete: true } &&
        target.LegacyContextHash is not null && target.LegacyContextHash == mark.ContextHash;

    public static ReviewContextChange? Explain(ReviewTarget target, ReviewCheckpoint? mark)
    {
        if (target.ContextFingerprint is not { } current) return null;
        var previous = mark?.ContextFingerprint;
        ReviewContextChange Result(string status, IReadOnlyList<string> changedNames, string note) =>
            new(status, previous?.Version, current.Version, changedNames, note);
        if (!current.Complete)
            return Result("context_unavailable", ["metadataReferences", "projectReferences"],
                "Some reference identities or dependency projects could not be resolved. These categories are candidates, not confirmed changes. Restore/resolve the intended project and reload before recording a checkpoint.");
        if (mark is null) return Result("no_baseline", [], "No previous checkpoint exists for this target.");
        if (target.ContextHash == mark.ContextHash)
            return Result("unchanged", [], "The loaded context matches the checkpoint. This is not a correctness or performance claim.");
        if (MatchesLegacy(target, mark))
            return Result("legacy_exact_match", [],
                "The exact v1 hash still matches. The old checkpoint was not rewritten. A new explicit checkpoint stores v2 components; a future legacy mismatch cannot be inferred from the target body alone.");
        if (previous is null)
            return Result("legacy_components_unavailable", [],
                "This legacy checkpoint stores only an aggregate hash. The cause of the mismatch cannot be recovered. Reinspect the intended context and explicitly record a new checkpoint; do not reset all marks or copy an old claim automatically.");
        if (previous.Version != current.Version)
            return Result("schema_changed", ["schema"], "Fingerprint schemas differ. Compare the intended context before recording a new checkpoint.");
        if (!Valid(previous, mark.ContextHash) || !Valid(current, target.ContextHash))
            return Result("components_inconsistent", [], "Saved component diagnostics do not match the aggregate hash. Do not use them to restore a checkpoint.");
        var old = previous.Components.ToDictionary(c => c.Name, c => c.Hash, StringComparer.Ordinal);
        var changes = current.Components.Where(c => old[c.Name] != c.Hash).Select(c => c.Name).ToList();
        if (previous.Complete != current.Complete) changes.Add("referenceAvailability");
        return Result("changed", changes.ToArray(),
            "These components changed in the loaded project/dependency closure. sourceFiles includes other methods; generatedSources is diagnostic only and is NOT ignored. Reload alone does not approve changed context.");
    }

    // List responses must not repeat two full manifests per target. Detailed target/context
    // and history calls retain them. Copy records; never mutate catalog/store objects.
    public static ReviewQueueItem CompactForQueue(ReviewQueueItem item) => item with
    {
        Target = item.Target with { ContextFingerprint = null },
        State = item.State with
        {
            ContextChange = null,
            Checkpoint = item.State.Checkpoint is { } checkpoint
                ? checkpoint with { ContextFingerprint = null } : null
        }
    };

    private static bool Valid(ReviewContextFingerprint value, string expectedHash)
    {
        if (value.Version != Version || value.Hash != expectedHash || value.Components is null ||
            value.Components.Count != ComponentNames.Count || value.Components.Any(c => c is null || !IsHash(c.Hash)) ||
            value.Components.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != ComponentNames.Count ||
            value.Components.Any(c => !ComponentNames.Contains(c.Name))) return false;
        return Create(value.Components.ToDictionary(c => c.Name, c => c.Hash, StringComparer.Ordinal), value.Complete).Hash == expectedHash;
    }
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
