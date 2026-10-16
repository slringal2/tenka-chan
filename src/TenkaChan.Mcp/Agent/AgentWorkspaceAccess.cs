using TenkaChan.Mcp.Tracking;

namespace TenkaChan.Mcp.Agent;

public static class AgentWorkspaceAccess
{
    public const int ReadWaitSeconds = 10;
    // Only gate admission is delayed. The caller MUST recheck the snapshot after admission.
    // This does not retry the operation or turn a write into a queued action.
    public static Task<bool> EnterAsync(SemaphoreSlim gate, bool readOnly, CancellationToken ct) =>
        gate.WaitAsync(readOnly ? TimeSpan.FromSeconds(ReadWaitSeconds) : TimeSpan.Zero, ct);

    public static bool MatchesLoadedPath(string requested, string? loaded)
    {
        if (string.IsNullOrWhiteSpace(requested) || !Path.IsPathFullyQualified(requested) ||
            Path.GetExtension(requested).ToLowerInvariant() is not (".sln" or ".slnx" or ".csproj"))
            throw new ArgumentException("path must be the absolute path of the loaded .sln/.slnx/.csproj.");
        return loaded is not null && ReviewProgress.CanonicalProject(requested) == ReviewProgress.CanonicalProject(loaded);
    }
}
