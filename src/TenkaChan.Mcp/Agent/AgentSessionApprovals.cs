using TenkaChan.Mcp.Security;
using TenkaChan.Mcp.Tracking;

namespace TenkaChan.Mcp.Agent;

public sealed record AgentSessionApproval(string ProjectPath, string[] Tools, DateTimeOffset ExpiresUtc,
    string GrantedSnapshotId, string Note);

/// <summary>
/// One bounded, non-sliding grant per authenticated journal session. The gateway supplies
/// the canonical project resolved from the current snapshot, never a caller-supplied scope.
/// A grant changes neither host switches nor workspace/experiment ownership.
/// </summary>
public sealed class AgentSessionApprovals(TimeProvider? time = null)
{
    public const int DefaultMinutes = 10, MaximumMinutes = 30;
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _sync = new();
    private readonly Dictionary<string, AgentSessionApproval> _grants = new(StringComparer.Ordinal);
    public static bool RequiresBuild(string tool) => tool is "build" or "run_companion" or "run_experiment";
    public static bool RequiresExecution(string tool) => tool is "jit" or "trace" or "run_companion" or "run_experiment";
    public static bool Supports(string tool) => RequiresBuild(tool) || RequiresExecution(tool);

    public AgentSessionApproval Grant(string session, string snapshot, string projectPath, string[] tools,
        int minutes, bool approved, bool allowBuild, bool allowExecution)
    {
        if (!approved) throw new UnauthorizedAccessException("Session approval requires explicit user consent. Set approved=true only for an agreed project, tool list and duration.");
        if (string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(snapshot))
            throw new ArgumentException("A live session and source snapshot are required.");
        if (minutes is < 1 or > MaximumMinutes) throw new ArgumentException("minutes must be 1..30.");
        if (tools is null || tools.Length is < 1 or > 5 || tools.Any(t => t is null || !Supports(t)))
            throw new ArgumentException("tools must contain 1..5 of build, jit, trace, run_companion, run_experiment. open_project is never session-approved.");
        var selected = tools.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        foreach (var tool in selected) RequirePermissions(tool, true, allowBuild, allowExecution);
        var scope = Scope(projectPath);
        lock (_sync)
        {
            Prune();
            if (!_grants.ContainsKey(session) && _grants.Count >= AgentJournal.MaxSessions)
                throw new InvalidOperationException("Session approval capacity reached. Finish an unused session; do not replace another client's approval.");
            var grant = new AgentSessionApproval(scope, selected, _time.GetUtcNow().AddMinutes(minutes), snapshot,
                "Applies only to the named project and tools, including discovered companions and generated experiments of that project. Code may change during this interval. Approval is checked on admission, is not renewed by use, and does not change restore options or approve opening/replacing a workspace. Granting permission does not start a process.");
            _grants[session] = grant;
            return Copy(grant);
        }
    }
    public bool Authorize(string session, string projectPath, string tool, bool approved,
        bool allowBuild, bool allowExecution)
    {
        if (!Supports(tool)) throw new ArgumentException("This tool does not support session approval.");
        var scope = Scope(projectPath);
        lock (_sync)
        {
            Prune();
            var consent = approved || (_grants.TryGetValue(session, out var grant) &&
                grant.ProjectPath == scope && grant.Tools.Contains(tool, StringComparer.Ordinal));
            RequirePermissions(tool, consent, allowBuild, allowExecution);
            return true;
        }
    }
    public AgentSessionApproval? Get(string session)
    {
        lock (_sync) { Prune(); return _grants.TryGetValue(session, out var grant) ? Copy(grant) : null; }
    }
    public bool Revoke(string session) { lock (_sync) return _grants.Remove(session); }
    public void RetainSessions(IEnumerable<string> liveSessions)
    {
        var live = liveSessions.ToHashSet(StringComparer.Ordinal);
        lock (_sync)
        {
            Prune();
            foreach (var session in _grants.Keys.Where(k => !live.Contains(k)).ToArray()) _grants.Remove(session);
        }
    }
    public static void RequirePermissions(string tool, bool approved, bool allowBuild, bool allowExecution)
    {
        if (RequiresBuild(tool)) AgentPermissionGate.Require("build/open", "TENKA_AGENT_ALLOW_BUILD", allowBuild, approved);
        if (RequiresExecution(tool)) AgentPermissionGate.Require("execution", "TENKA_AGENT_ALLOW_EXECUTION", allowExecution, approved);
    }
    private static string Scope(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !Path.GetExtension(path).Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Approval scope must be an absolute, loaded .csproj path.");
        return ReviewProgress.CanonicalProject(path);
    }
    private void Prune()
    {
        var now = _time.GetUtcNow();
        foreach (var key in _grants.Where(p => p.Value.ExpiresUtc <= now).Select(p => p.Key).ToArray()) _grants.Remove(key);
    }
    private static AgentSessionApproval Copy(AgentSessionApproval grant) => grant with { Tools = grant.Tools.ToArray() };
}
