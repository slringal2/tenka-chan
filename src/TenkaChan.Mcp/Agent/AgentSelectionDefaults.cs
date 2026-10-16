using System.Text.Json;
using TenkaChan.Mcp.Analysis;

namespace TenkaChan.Mcp.Agent;

/// <summary>Resolve once under the workspace gate; subsequent operations still check the pinned snapshot.</summary>
public static class AgentSelectionDefaults
{
    public static string? OptionalId(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("Tool arguments must be a JSON object.");
        if (!arguments.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ArgumentException($"Invalid field: {name}. Supply a non-empty string, or omit it to use the documented default.");
        return value.GetString();
    }
    public static bool NeedsResolution(JsonElement arguments, bool requireProject) =>
        OptionalId(arguments, "snapshotId") is null || requireProject && OptionalId(arguments, "projectId") is null;

    public static JsonElement Resolve(JsonElement arguments, string currentSnapshotId,
        IReadOnlyList<ExplorerProject> projects, string? openedPath, bool requireProject)
    {
        if (string.IsNullOrWhiteSpace(currentSnapshotId))
            throw new InvalidOperationException("workspace_not_loaded: Open a trusted project before using source tools.");
        var requested = OptionalId(arguments, "snapshotId");
        if (requested is not null && requested != currentSnapshotId)
            throw new InvalidOperationException("snapshot_changed: The explicit snapshotId is stale. Read tenka_context and choose current IDs. It was not replaced automatically.");
        var values = arguments.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        values["snapshotId"] = JsonSerializer.SerializeToElement(currentSnapshotId);
        if (requireProject)
        {
            var projectId = OptionalId(arguments, "projectId");
            if (projectId is null)
            {
                // A directly opened .csproj is active even if its dependencies are also loaded.
                // A multi-project solution has no server-side active project; never choose its first row.
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                var root = openedPath is null ? null : Path.GetFullPath(openedPath);
                var matching = projects.Where(p => root is not null &&
                    string.Equals(Path.GetFullPath(p.Path), root, comparison)).ToArray();
                if (matching.Length == 1) projectId = matching[0].Id;
                else if (matching.Length == 0 && projects.Count == 1) projectId = projects[0].Id;
                else throw new ArgumentException("ambiguous_project: projectId is required because no unique active project exists. Read tenka_context workspace.projects and select one; no project was chosen or executed.");
            }
            if (!projects.Any(p => p.Id == projectId))
                throw new ArgumentException("invalid_project: projectId is not in the current snapshot. Read tenka_context; an explicit ID is never replaced automatically.");
            values["projectId"] = JsonSerializer.SerializeToElement(projectId);
        }
        return JsonSerializer.SerializeToElement(values);
    }
}
