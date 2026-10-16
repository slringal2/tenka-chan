using System.Globalization;

namespace TenkaChan.Mcp.Agent;

public sealed record AgentSessionOptions(int MaximumSessions = 16, int IdleTimeoutMinutes = 15)
{
    public AgentSessionOptions Validate()
    {
        if (MaximumSessions is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(MaximumSessions), "Use 1 through 128 sessions.");
        if (IdleTimeoutMinutes is < 1 or > 1440) throw new ArgumentOutOfRangeException(nameof(IdleTimeoutMinutes), "Use 1 through 1440 minutes.");
        return this;
    }
    public static AgentSessionOptions FromEnvironment() => Parse(
        Environment.GetEnvironmentVariable("TENKA_AGENT_MAX_SESSIONS"),
        Environment.GetEnvironmentVariable("TENKA_AGENT_IDLE_TTL_MINUTES"));

    public static AgentSessionOptions Parse(string? maximumSessions, string? idleTimeoutMinutes) => new AgentSessionOptions(
        Setting(maximumSessions, "TENKA_AGENT_MAX_SESSIONS", 16, 1, 128),
        Setting(idleTimeoutMinutes, "TENKA_AGENT_IDLE_TTL_MINUTES", 15, 1, 1440)).Validate();

    private static int Setting(string? value, string name, int fallback, int minimum, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) || result < minimum || result > maximum)
            throw new ArgumentException($"{name} must be an integer from {minimum} through {maximum}.");
        return result;
    }
}

public sealed record AgentSessionList(string InstanceId, int MaximumSessions, int IdleTimeoutMinutes,
    int ActiveSessions, int AvailableSlots, IReadOnlyList<AgentSessionView> Items, string Note);
