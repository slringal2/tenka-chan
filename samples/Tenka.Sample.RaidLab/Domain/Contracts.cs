namespace Tenka.Sample.RaidLab.Domain;

public sealed record PlayerProfile(int Id, int GuildId, string Name, int[] EquippedSkills);
public sealed record SkillDefinition(int Id, int BonusPercent);
public sealed record CombatEvent(long EventId, int PlayerId, int SkillId, int Damage, bool Cancelled = false);
public sealed record RaidDataset(PlayerProfile[] Players, SkillDefinition[] Skills, CombatEvent[] Events);
public sealed record ReportOptions(int MinimumDamage = 5, int TopPlayers = 20)
{
    public void Validate()
    {
        if (MinimumDamage < 0 || TopPlayers is < 1 or > 2000) throw new ArgumentOutOfRangeException(nameof(ReportOptions));
    }
}
public sealed record PlayerScore(int PlayerId, int GuildId, long Damage, int Hits);
public sealed record GuildScore(int GuildId, long Damage, int Hits, int Participants);
public sealed record RaidReport(int InputEvents, int AcceptedEvents, int DuplicateEvents, int RejectedEvents,
    PlayerScore[] Players, GuildScore[] Guilds);
public sealed record ReportEnvelope(RaidReport Report, string Text);

public interface IRaidAnalyzer
{
    RaidReport Analyze(RaidDataset data, ReportOptions options, CancellationToken cancellationToken = default);
}
public interface IRaidRepository
{
    Task<RaidDataset> ReadAsync(CancellationToken cancellationToken = default);
}

public static class DatasetContract
{
    public static void Validate(RaidDataset data, ReportOptions options, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(data); ArgumentNullException.ThrowIfNull(options);
        options.Validate(); ArgumentNullException.ThrowIfNull(data.Players);
        ArgumentNullException.ThrowIfNull(data.Skills); ArgumentNullException.ThrowIfNull(data.Events);
        if (data.Players.Length > 2000 || data.Skills.Length > 1024 || data.Events.Length > 100000)
            throw new ArgumentException("Dataset exceeds the bounded sample workload.");
        var players = new HashSet<int>(); var skills = new HashSet<int>();
        foreach (var p in data.Players)
        {
            ct.ThrowIfCancellationRequested();
            if (p is null || p.Id <= 0 || p.GuildId <= 0 || p.Name is null || p.EquippedSkills is null ||
                p.EquippedSkills.Length > 1024 || !players.Add(p.Id)) throw new ArgumentException("Invalid or duplicate player metadata.");
        }
        foreach (var s in data.Skills)
            if (s is null || s.Id <= 0 || s.BonusPercent is < 0 or > 100 || !skills.Add(s.Id))
                throw new ArgumentException("Invalid or duplicate skill metadata.");
        // Invalid event values are rejected by the analyzers, but a null event is malformed input.
        foreach (var e in data.Events) { ct.ThrowIfCancellationRequested(); if (e is null) throw new ArgumentException("Null event."); }
    }
}
