using System.Runtime.CompilerServices;
using Tenka.Sample.RaidLab.Domain;

namespace Tenka.Sample.RaidLab.Application;

public sealed class IndexedRaidAnalyzer : IRaidAnalyzer
{
    private sealed class Aggregate
    {
        public long Damage;
        public int Hits;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public RaidReport Analyze(RaidDataset data, ReportOptions options, CancellationToken cancellationToken = default)
    {
        DatasetContract.Validate(data, options, cancellationToken);
        // These indexes are rebuilt for EVERY invocation and their cost is included in measurements.
        var players = data.Players.ToDictionary(p => p.Id);
        var equipment = data.Players.ToDictionary(p => p.Id, p => p.EquippedSkills.ToHashSet());
        var skills = data.Skills.ToDictionary(s => s.Id);
        var seen = new HashSet<long>(); var totals = new Dictionary<int, Aggregate>();
        var accepted = 0; var duplicates = 0; var rejected = 0;
        foreach (var e in data.Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (e.EventId <= 0) { rejected++; continue; }
            if (!seen.Add(e.EventId)) { duplicates++; continue; }
            if (e.Cancelled || e.Damage < options.MinimumDamage || !players.TryGetValue(e.PlayerId, out var player) ||
                !skills.TryGetValue(e.SkillId, out var skill) || !equipment[player.Id].Contains(e.SkillId))
            { rejected++; continue; }
            if (!totals.TryGetValue(player.Id, out var row)) totals.Add(player.Id, row = new());
            row.Damage += (long)e.Damage * (100 + skill.BonusPercent) / 100;
            row.Hits++; accepted++;
        }
        var rows = totals.Select(kv => new PlayerScore(kv.Key, players[kv.Key].GuildId, kv.Value.Damage, kv.Value.Hits)).ToArray();
        var guilds = rows.GroupBy(r => r.GuildId)
            .Select(g => new GuildScore(g.Key, g.Sum(r => r.Damage), g.Sum(r => r.Hits), g.Count()))
            .OrderByDescending(r => r.Damage).ThenBy(r => r.GuildId).ToArray();
        return new(data.Events.Length, accepted, duplicates, rejected,
            rows.OrderByDescending(r => r.Damage).ThenBy(r => r.PlayerId).Take(options.TopPlayers).ToArray(), guilds);
    }
}
