using TenkaChan.Mcp.Agent;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using TenkaChan.Mcp.Tracking;

namespace TenkaChan.Web;

/// <summary>One snapshot gate for target, source IDs and bounded metadata candidate discovery.</summary>
public static class ReviewContextReader
{
    public static Task<object> ReadAsync(DashboardState dashboard, WorkspaceGuard guard,
        WorkspaceBinaryDiscovery discovery, ReviewProgressStore store, string snapshotId,
        string targetId, bool includeBinary, CancellationToken ct) =>
        dashboard.ExploreAsync(snapshotId, async (index, roslyn) =>
        {
            var catalog = await index.ReviewCatalogAsync(guard, ct);
            var target = catalog.Require(targetId);
            var stale = await catalog.StaleProjectsAsync(guard, false, ct);
            var state = ReviewProgress.State(target, store.Latest().GetValueOrDefault(target.Id), stale.Contains(target.ProjectId));
            var method = target.Kind == "method" ? index.Method(target.SelectionId) : null;
            var selection = ReviewContextPlan.Selection(index.SnapshotId, target, method);
            object source = method is null ? (object)await index.TypeAsync(selection.TypeId, ct) :
                new { method, inspection = await roslyn.InspectAsync(method.Id, true, ct) };
            var candidates = new List<ReviewBinaryCandidates>();
            var failures = new List<object>();
            var assemblyCount = 0;
            var binaryStatus = !includeBinary ? "not_requested" : method is null ? "type_target" :
                state.RequiresReload ? "source_reload_required" : "no_build_output";
            if (includeBinary && method is not null && !state.RequiresReload)
            {
                var assemblies = ExplorerProjectActions.Assemblies(index, discovery)
                    .Where(a => a.ProjectId == method.ProjectId).OrderBy(a => a.Id, StringComparer.Ordinal).ToArray();
                assemblyCount = assemblies.Length;
                var inspector = new BinarySourceInspector(guard);
                foreach (var assembly in assemblies.Take(ReviewContextPlan.MaximumAssemblies))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var binary = inspector.Methods(assembly.AssemblyPath, method.MetadataType + "::" + method.MetadataName, 2000);
                        candidates.Add(ReviewContextPlan.Binary(selection, method, assembly, binary));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or BadImageFormatException)
                    {
                        failures.Add(new { assemblyId = assembly.Id, status = "binary_unavailable", error = ex.GetType().Name,
                            nextAction = "Inspect this output with tenka_binary_methods. Rebuild only with explicit approval if the output is missing or stale." });
                    }
                }
                binaryStatus = candidates.Any(c => c.Candidates.Count > 0) ? "candidates" :
                    failures.Count > 0 ? "binary_unavailable" : assemblyCount > 0 ? "no_matching_method" : "no_build_output";
            }
            return (object)new
            {
                snapshotId = index.SnapshotId, selection, target, state, source,
                referenceDiagnostics = ReviewReferenceDiagnostics.Summarize(catalog.ReferencesFor(target.ProjectId)),
                markBasis = new { snapshotId = index.SnapshotId, targetId = target.Id,
                    target.SourceHash, target.ContextHash, expectedRevision = state.Revision },
                binary = new { status = binaryStatus, assemblyCount,
                    limited = assemblyCount > ReviewContextPlan.MaximumAssemblies || candidates.Any(c => c.Limited),
                    assemblies = candidates, failures,
                    note = "Candidates match metadata type/name only. Choose the exact overload and build configuration; even a single candidate is NOT proof of source/build identity. Pass a candidate's arguments unchanged to tenka_il, which rechecks snapshot and assembly SHA-256. For omitted assemblies/candidates, use tenka_context and tenka_binary_methods. No build or target execution was performed." },
                contextHashSemantics = "Context fingerprint includes raw sourceFiles in the project/dependency closure, including whitespace-only edits (only line endings are normalized). sourceChanged and contextChanged may both be true; the original hash is not rewritten.",
                note = ReviewProgress.Note
            };
        }, ct, waitForRead: true);
}
