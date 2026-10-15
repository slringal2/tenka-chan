using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Tenka.Sample.RaidLab.Domain;

namespace Tenka.Sample.RaidLab.Application;

public static class ReportFormatter
{
    private static string Header(RaidReport r) => FormattableString.Invariant($"events|{r.InputEvents}|{r.AcceptedEvents}|{r.DuplicateEvents}|{r.RejectedEvents}\n");
    private static string Player(PlayerScore p) => FormattableString.Invariant($"player|{p.PlayerId}|{p.GuildId}|{p.Damage}|{p.Hits}\n");
    private static string Guild(GuildScore g) => FormattableString.Invariant($"guild|{g.GuildId}|{g.Damage}|{g.Hits}|{g.Participants}\n");
    public static string Build(RaidReport report)
    {
        var text = Header(report);
        foreach (var p in report.Players) text += Player(p);
        foreach (var g in report.Guilds) text += Guild(g);
        return text;
    }
    // Stable digest is useful for comparisons; it is not a performance measurement or correctness proof.
    public static string Fingerprint(RaidReport report) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Build(report))));
}
