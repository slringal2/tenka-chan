using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TenkaChan.Mcp.Analysis;

public sealed record ProfileMethodIdentity(string MethodId, string QualifiedMember);

/// <summary>
/// Read-only Speedscope sampled/evented import. Method totals are computed
/// before frame display truncation. No executable, PDB path or URL is followed.
/// </summary>
public static class SpeedscopeHotspotParser
{
    private sealed class Cost
    {
        public int Observations;
        public double Inclusive;
        public double Exclusive;
    }

    public static SampledHotspotReport Read(string path,
        IReadOnlyList<ProfileMethodIdentity> methods, int profileIndex = 0, int limit = 100)
    {
        var bytes = BoundedBinaryFile.Read(path, 25_000_000);
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, true);
        return Parse(reader.ReadToEnd(), path, methods, profileIndex, limit) with
        {
            SourceSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            SourceIdentityKind = "file_bytes_sha256"
        };
    }

    public static SampledHotspotReport Parse(string source, string sourcePath,
        IReadOnlyList<ProfileMethodIdentity> methods, int profileIndex = 0, int limit = 100)
    {
        if (source.Length > 25_000_000) throw new InvalidDataException("Profile exceeds 25 MB character budget.");
        limit = Math.Clamp(limit, 1, 500);
        using var document = JsonDocument.Parse(source, new JsonDocumentOptions { MaxDepth = 160 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("shared", out var shared) || shared.ValueKind != JsonValueKind.Object ||
            !shared.TryGetProperty("frames", out var frames) || frames.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Not a Speedscope profile.");
        if (frames.GetArrayLength() > 50000) throw new InvalidDataException("Too many frames.");
        if (profileIndex < 0 || profileIndex >= profiles.GetArrayLength())
            throw new ArgumentOutOfRangeException(nameof(profileIndex));
        var profile = profiles[profileIndex];
        var kind = StringProperty(profile, "type");
        if (kind is not ("sampled" or "evented")) throw new InvalidDataException("Unsupported profile type.");
        var name = StringProperty(profile, "name");
        if (name.Length > 1000) name = name[..1000];
        var unit = StringProperty(profile, "unit");
        if (unit is not ("seconds" or "milliseconds" or "microseconds" or "nanoseconds" or "bytes" or "none"))
            throw new InvalidDataException("Invalid profile unit.");
        var names = frames.EnumerateArray().Select(frame =>
        {
            var text = StringProperty(frame, "name");
            if (text.Length > 4096) throw new InvalidDataException("Frame label exceeds 4096 characters.");
            return text; // Never match a truncated label to a different method.
        }).ToArray();
        var matcher = new ProfileFrameMatcher(methods);
        var associations = names.Select(matcher.Match).ToArray();
        var frameCosts = names.Select(_ => new Cost()).ToArray();
        var methodCosts = new Dictionary<string, Cost>(StringComparer.Ordinal);
        var methodNames = methods.GroupBy(m => m.MethodId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().QualifiedMember, StringComparer.Ordinal);
        var total = 0d;
        var unassigned = 0d;
        var observations = 0;

        void Attribute(IReadOnlyList<int> stack, double weight)
        {
            if (!double.IsFinite(weight) || weight < 0) throw new InvalidDataException("Invalid profile weight.");
            total += weight;
            if (!double.IsFinite(total)) throw new InvalidDataException("Profile total overflow.");
            if (weight == 0) return;
            var seenFrames = new HashSet<int>();
            var seenMethods = new HashSet<string>(StringComparer.Ordinal);
            foreach (var frame in stack)
            {
                if (seenFrames.Add(frame))
                {
                    frameCosts[frame].Inclusive += weight;
                    frameCosts[frame].Observations++;
                }
                var association = associations[frame];
                if (association.Length != 1 || !seenMethods.Add(association[0])) continue;
                if (!methodCosts.TryGetValue(association[0], out var cost))
                    methodCosts.Add(association[0], cost = new Cost());
                cost.Inclusive += weight;
                cost.Observations++;
            }
            if (stack.Count == 0) { unassigned += weight; return; }
            var leaf = stack[stack.Count - 1];
            frameCosts[leaf].Exclusive += weight;
            if (associations[leaf].Length == 1)
                methodCosts[associations[leaf][0]].Exclusive += weight;
        }

        if (kind == "sampled")
        {
            if (!profile.TryGetProperty("samples", out var samples) || samples.ValueKind != JsonValueKind.Array ||
                !profile.TryGetProperty("weights", out var weights) || weights.ValueKind != JsonValueKind.Array ||
                samples.GetArrayLength() != weights.GetArrayLength() || samples.GetArrayLength() > 200000)
                throw new InvalidDataException("Invalid sample stacks/weights.");
            observations = samples.GetArrayLength();
            for (int i = 0; i < observations; i++)
            {
                var sample = samples[i];
                if (sample.ValueKind != JsonValueKind.Array || sample.GetArrayLength() > 1000)
                    throw new InvalidDataException("Invalid sample stack depth.");
                var stack = sample.EnumerateArray().Select(frame => FrameIndex(frame, names.Length)).ToArray();
                Attribute(stack, Number(weights[i]));
            }
        }
        else
        {
            if (!profile.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array ||
                events.GetArrayLength() > 200000) throw new InvalidDataException("Invalid event list.");
            double start = NumberProperty(profile, "startValue"), end = NumberProperty(profile, "endValue");
            if (end < start) throw new InvalidDataException("Invalid evented boundaries.");
            var stack = new List<int>();
            double previous = start;
            foreach (var ev in events.EnumerateArray())
            {
                var operation = StringProperty(ev, "type");
                var time = NumberProperty(ev, "at");
                if (time < previous || time > end) throw new InvalidDataException("Non-monotonic event times.");
                if (!ev.TryGetProperty("frame", out var frameNode)) throw new InvalidDataException("Missing event frame.");
                int frame = FrameIndex(frameNode, names.Length);
                if (time > previous) { Attribute(stack, time - previous); observations++; }
                if (operation == "O")
                {
                    if (stack.Count >= 1000) throw new InvalidDataException("Event stack exceeds maximum depth.");
                    stack.Add(frame);
                }
                else if (operation == "C" && stack.Count > 0 && stack[^1] == frame)
                    stack.RemoveAt(stack.Count - 1);
                else throw new InvalidDataException("Unknown or non-LIFO event operation.");
                previous = time;
            }
            if (stack.Count != 0) throw new InvalidDataException("Unbalanced evented stack.");
            if (end > previous) { Attribute(stack, end - previous); observations++; }
        }
        if (total <= 0) throw new InvalidDataException("No positive profile weight.");
        var resultFrames = new List<SampledFrameCost>();
        var contributing = new Dictionary<string, int>(StringComparer.Ordinal);
        int linked = 0, ambiguous = 0;
        double unmapped = 0;
        for (int i = 0; i < names.Length; i++)
        {
            var cost = frameCosts[i];
            if (cost.Inclusive <= 0) continue;
            var match = associations[i];
            if (match.Length == 1)
            {
                linked++;
                contributing[match[0]] = contributing.GetValueOrDefault(match[0]) + 1;
            }
            else
            {
                unmapped += cost.Exclusive;
                if (match.Length > 1) ambiguous++;
            }
            resultFrames.Add(new SampledFrameCost(names[i], cost.Observations,
                cost.Inclusive, cost.Exclusive, Percent(cost.Inclusive, total), Percent(cost.Exclusive, total),
                match.Length == 1 ? match[0] : null,
                match.Length == 1 ? "matched_exact_qualified_name" : match.Length > 1 ? "ambiguous_overload_or_type" : "unmatched"));
        }
        return new SampledHotspotReport(sourcePath, name, unit, observations, total,
            resultFrames.Count, linked, ambiguous,
            resultFrames.OrderByDescending(f => f.ExclusiveWeight).ThenByDescending(f => f.InclusiveWeight)
                .ThenBy(f => f.FrameName, StringComparer.Ordinal).Take(limit).ToArray(),
            [
                "Observations describe this selected profile only; units and collector semantics do not by themselves prove on-CPU time or a regression.",
                "Qualified-name association is not a build/MVID match. Overloads and unknown frames remain unassociated.",
                "Method inclusive attribution counts each associated method once per observation, including recursive frame aliases. Do not sum inclusive weights across methods.",
                "MethodCosts cover all frames before display limits. Unmapped and empty-stack weights remain in the denominator.",
                "Native JIT and IL source evidence still require their own matching build identities; a matching profile name alone cannot bind these builds."
            ])
        {
            ProfileType = kind, ProfileIndex = profileIndex,
            SourceSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))),
            SourceIdentityKind = "decoded_utf8_sha256", CompleteMethodAttribution = true,
            MethodCosts = methodCosts.OrderByDescending(p => p.Value.Exclusive)
                .ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => new ProfileMethodCost(p.Key,
                    methodNames[p.Key], p.Value.Observations, p.Value.Inclusive, p.Value.Exclusive,
                    contributing.GetValueOrDefault(p.Key))).ToArray(),
            UnmappedExclusiveWeight = unmapped, UnassignedWeight = unassigned
        };
    }

    private static string StringProperty(JsonElement node, string key)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Missing string property: " + key);
        return value.GetString()!;
    }
    private static double NumberProperty(JsonElement node, string key)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(key, out var value))
            throw new InvalidDataException("Missing number property: " + key);
        return Number(value);
    }
    private static double Number(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) ||
            !double.IsFinite(number) || number < 0) throw new InvalidDataException("Invalid profile number.");
        return number;
    }
    private static int FrameIndex(JsonElement frame, int count)
    {
        if (frame.ValueKind != JsonValueKind.Number || !frame.TryGetInt32(out var index) || index < 0 || index >= count)
            throw new InvalidDataException("Invalid frame index.");
        return index;
    }
    private static double Percent(double value, double total) => Math.Round(value / total * 100, 3);
}
