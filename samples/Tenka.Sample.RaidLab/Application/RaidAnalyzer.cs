using Tenka.Sample.RaidLab.Domain;

namespace Tenka.Sample.RaidLab.Application;

public sealed class RaidAnalyzer : IRaidAnalyzer
{
    private sealed record Accepted(int PlayerId, int GuildId, long Damage);
    public RaidReport Analyze(RaidDataset data, ReportOptions options, CancellationToken cancellationToken = default)
    {
        DatasetContract.Validate(data, options, cancellationToken);
        var seen = new List<long>(); var accepted = new List<Accepted>(); var duplicates = 0; var rejected = 0;
        foreach (var e in data.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (e.EventId <= 0) { rejected++; continue; }
            if (seen.Contains(e.EventId)) { duplicates++; continue; }
            seen.Add(e.EventId); // The FIRST positive ID wins, even if its event is invalid.
            if (e.Cancelled || e.Damage < options.MinimumDamage) { rejected++; continue; }
            var player = data.Players.Where(p => p.Id == e.PlayerId).ToList().FirstOrDefault();
            var skill = data.Skills.Where(s => s.Id == e.SkillId).ToArray().FirstOrDefault();
            if (player is null || skill is null || !player.EquippedSkills.Contains(e.SkillId)) { rejected++; continue; }
            accepted.Add(new(player.Id, player.GuildId, (long)e.Damage * (100 + skill.BonusPercent) / 100));
        }
        var rows = new List<PlayerScore>();
        foreach (var player in data.Players)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = accepted.Where(e => e.PlayerId == player.Id).ToArray();
            if (entries.Length != 0) rows.Add(new(player.Id, player.GuildId, entries.Sum(e => e.Damage), entries.Length));
        }
        var guilds = new List<GuildScore>();
        foreach (var guildId in rows.Select(r => r.GuildId).Distinct())
        {
            var group = rows.Where(r => r.GuildId == guildId).ToList();
            guilds.Add(new(guildId, group.Sum(r => r.Damage), group.Sum(r => r.Hits), group.Count));
        }
        return new(data.Events.Length, accepted.Count, duplicates, rejected,
            rows.OrderByDescending(r => r.Damage).ThenBy(r => r.PlayerId).Take(options.TopPlayers).ToArray(),
            guilds.OrderByDescending(r => r.Damage).ThenBy(r => r.GuildId).ToArray());
    }
}
