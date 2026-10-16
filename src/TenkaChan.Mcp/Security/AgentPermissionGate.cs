namespace TenkaChan.Mcp.Security;

/// <summary>
/// Separates the two independent gates that guard agent build/open and execution:
/// the server-side environment switch and the explicit per-call approval.
/// A combined message is misleading because enabling the environment variable alone
/// does not authorize a call, and approving a call does nothing while the switch is off.
/// The thrown text names exactly which gate is closed so a caller can act without guessing.
/// </summary>
public static class AgentPermissionGate
{
    public static void Require(string scope, string enableVariable, bool allowedByServer, bool approvedByCall)
    {
        if (!allowedByServer)
            throw new UnauthorizedAccessException(
                $"Agent {scope} is disabled on the server. Set {enableVariable}=1 and restart the server, then retry. Per-call approved=true does not override this switch.");
        if (!approvedByCall)
            throw new UnauthorizedAccessException(
                $"Agent {scope} requires explicit per-call approval. Re-invoke this tool with approved=true; the server switch {enableVariable} is already enabled.");
    }
}
