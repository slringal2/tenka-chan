using Tenka.Sample.RaidLab.Domain;

namespace Tenka.Sample.RaidLab.Infrastructure;

public static class DeterministicRaidData
{
    // Defined uint arithmetic, not System.Random: identical data across platforms/runtimes.
    public static RaidDataset Create(int players = 200, int events = 6000, uint seed = 17)
    {
        if (players is < 1 or > 2000 || events is < 0 or > 100000) throw new ArgumentOutOfRangeException(nameof(players));
        uint state = seed;
        uint Next() { state = unchecked(state * 1664525u + 1013904223u); return state; }
        var roster = Enumerable.Range(1, players).Select(i => new PlayerProfile(i, (i - 1) % 8 + 1,
            "Player-" + i, [1, 2, 3, 4])).ToArray();
        var skills = Enumerable.Range(1, 6).Select(i => new SkillDefinition(i, i * 10)).ToArray();
        var stream = new CombatEvent[events];
        for (var i = 0; i < events; i++)
        {
            var player = (int)(Next() % (uint)players) + 1;
            var skill = (int)(Next() % 6) + 1;
            var damage = (int)(Next() % 300);
            stream[i] = new(i + 1L, i % 37 == 0 ? players + 7 : player, skill,
                i % 43 == 0 ? -1 : damage, i % 29 == 0);
            if (i > 0 && i % 17 == 0) stream[i] = stream[i - 1] with { Damage = 999 };
        }
        return new(roster, skills, stream);
    }
}
