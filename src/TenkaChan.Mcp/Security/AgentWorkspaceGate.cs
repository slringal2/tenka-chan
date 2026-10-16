namespace TenkaChan.Mcp.Security;

/// <summary>
/// Decides whether an agent open request may proceed and, when a workspace is already
/// loaded under a different snapshot, explains how to retry with the concrete value
/// instead of only telling the caller that "the workspace changed".
/// </summary>
public static class AgentWorkspaceGate
{
    public const string None = "(none loaded)";

    public static bool Matches(string? expectedSnapshotId, string? currentSnapshotId) =>
        string.Equals(expectedSnapshotId, currentSnapshotId, StringComparison.Ordinal);

    public static string ConflictMessage(string? expectedSnapshotId, string? currentSnapshotId) =>
        currentSnapshotId is null
            ? $"Workspace changed. Current snapshot is {None}, so expectedSnapshotId={Format(expectedSnapshotId)} cannot be matched. " +
              "Retry with approved=true and no expectedSnapshotId to open the project."
            : $"Workspace changed. Current snapshot is \"{currentSnapshotId}\" but the request had expectedSnapshotId={Format(expectedSnapshotId)}. " +
              $"Retry tenka_open_project with expectedSnapshotId=\"{currentSnapshotId}\" (or read tenka_context first).";

    private static string Format(string? expectedSnapshotId) =>
        expectedSnapshotId is null ? "(omitted)" : $"\"{expectedSnapshotId}\"";
}
