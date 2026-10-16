using System.Text.Json;

namespace TenkaChan.Mcp.Agent;

public sealed record AgentSessionRequest(string ClientName = "external-agent", string Goal = "");
public sealed record AgentCallRequest(string SessionId, string RequestId, string Tool, JsonElement Arguments);
public sealed record AgentNote(string Kind, string Message, string[]? EvidenceCallIds = null);
public sealed record AgentEvent(long Sequence, string SessionId, string? CallId, string Kind,
    string Tool, string Message, string? SnapshotId, string? ObservedStatus, DateTimeOffset At);
public sealed record AgentSessionView(string Id, string ClientName, string Goal, string Status,
    DateTimeOffset LastSeen, int Calls, string? ActiveTool);
public sealed record AgentReply(string CallId, string Outcome, string? ObservedStatus,
    string? SnapshotId, string? Error, JsonElement? Data, bool ResultAvailable,
    string[] AvailableFields)
{
    public string? InvestigationId { get; init; }
    public string? PersistenceWarning { get; init; }
}
public sealed record AgentMonitor(string InstanceId, bool Enabled, bool AllowBuild, bool AllowExecution,
    long LastSequence, IReadOnlyList<AgentSessionView> Sessions, IReadOnlyList<AgentEvent> Events,
    string Note);
