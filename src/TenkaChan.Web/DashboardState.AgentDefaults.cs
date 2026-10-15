using System.Text.Json;
using TenkaChan.Mcp.Agent;

namespace TenkaChan.Web;

public sealed partial class DashboardState
{
    public async Task<JsonElement> ResolveAgentSelectionAsync(JsonElement arguments, bool requireProject, CancellationToken ct)
    {
        var requested = AgentSelectionDefaults.OptionalId(arguments, "snapshotId");
        // Resolution and inspection share the existing gate. We release it with an EXPLICIT
        // snapshot, not a wildcard: the actual operation rechecks it under the same gate.
        return (JsonElement)await ExploreAsync(requested, (index, _) => Task.FromResult((object)
            AgentSelectionDefaults.Resolve(arguments, index.SnapshotId, index.Projects, _path, requireProject)), ct);
    }
}
