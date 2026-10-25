using System.Text.Json;

namespace TenkaChan.Mcp.Analysis;

/// <summary>
/// Fully-qualified method name as emitted by Roslyn, for conservative
/// Speedscope stack-frame correlation. Overloads with identical frame labels
/// are intentionally left ambiguous rather than attached to a wrong method.
/// </summary>
public sealed record ProfileMethodIdentity(string MethodId, string QualifiedMember);

/// <summary>
/// Import speedscope sampled/evented JSON exported from a representative workload,
/// e.g. by dotnet-trace conversion or another compatible profiler.
/// No target code or profiler process is started by this parser.
/// </summary>
public static class SpeedscopeHotspotParser
{
    private sealed class Accumulator(string name)
    {
        public readonly string Name = name;
        public int Samples;
        public double Inclusive;
        public double Exclusive;
    }

    public static SampledHotspotReport Read(
        string path, IReadOnlyList<ProfileMethodIdentity> methods,
        int profileIndex = 0, int limit = 100)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Speedscope profile missing.", path);
        if (new FileInfo(path).Length > 25_000_000)
            throw new InvalidDataException("Speedscope file exceeds 25 MB.");
        return Parse(File.ReadAllText(path), path, methods, profileIndex, limit);
    }

    public static SampledHotspotReport Parse(
        string source, string sourcePath, IReadOnlyList<ProfileMethodIdentity> methods,
        int profileIndex = 0, int limit = 100)
    {
        if (source.Length > 25_000_000)
            throw new InvalidDataException("Speedscope input exceeds the maximum size.");
        limit = Math.Clamp(limit, 1, 500);
        using var document = JsonDocument.Parse(source, new JsonDocumentOptions
        {
            MaxDepth = 160, AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("shared", out var shared) ||
            !shared.TryGetProperty("frames", out var frames) ||
            frames.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("profiles", out var profiles) ||
            profiles.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Not a speedscope profile.");

        if (frames.GetArrayLength() > 50000)
            throw new InvalidDataException("Too many frames.");
        if (profileIndex < 0 || profileIndex >= profiles.GetArrayLength())
            throw new ArgumentOutOfRangeException(nameof(profileIndex));
        var profile = profiles[profileIndex];
        if (!profile.TryGetProperty("type", out var kind) ||
            kind.ValueKind != JsonValueKind.String ||
            kind.GetString() is not ("sampled" or "evented"))
            throw new InvalidDataException(
                "Only speedscope sampled and evented profiles are supported.");
        var profileType = kind.GetString()!;
        var name = profile.TryGetProperty("name", out var nameProp) &&
            nameProp.ValueKind == JsonValueKind.String
                ? (nameProp.GetString() ?? "profile") : "profile";
        if (!profile.TryGetProperty("unit", out var unitProp) ||
            unitProp.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Missing sample profile unit.");
        var unit = unitProp.GetString() ?? "none";
        if (unit is not ("seconds" or "milliseconds" or "microseconds" or
            "nanoseconds" or "bytes" or "none"))
            throw new InvalidDataException("Unexpected speedscope unit.");
        var names = frames.EnumerateArray().Select(frame =>
        {
            if (frame.ValueKind != JsonValueKind.Object ||
                !frame.TryGetProperty("name", out var property) ||
                property.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Frame must have a name.");
            var value = property.GetString() ?? "";
            return value.Length <= 400 ? value : value[..400];
        }).ToArray();
        var data = names.Select(name => new Accumulator(name)).ToArray();
        var total = 0d;
        var observationsCount = 0;
        if (profileType == "sampled")
        {
            if (!profile.TryGetProperty("samples", out var stacks) ||
                !profile.TryGetProperty("weights", out var weights) ||
                stacks.ValueKind != JsonValueKind.Array ||
                weights.ValueKind != JsonValueKind.Array ||
                stacks.GetArrayLength() != weights.GetArrayLength() ||
                stacks.GetArrayLength() > 200000)
                throw new InvalidDataException("Invalid sampled profile stacks/weights.");
            observationsCount = stacks.GetArrayLength();

            for (var i = 0; i < observationsCount; i++)
            {
                var weightNode = weights[i];
                if (weightNode.ValueKind != JsonValueKind.Number ||
                    !weightNode.TryGetDouble(out var weight) ||
                    !double.IsFinite(weight) || weight < 0)
                    throw new InvalidDataException("Non-finite or negative sample weight.");
                total += weight;
                if (!double.IsFinite(total))
                    throw new InvalidDataException("Profile weight sum overflow.");
                var stackNode = stacks[i];
                if (stackNode.ValueKind != JsonValueKind.Array ||
                    stackNode.GetArrayLength() > 1000)
                    throw new InvalidDataException("Invalid stack frame depth.");
                var unique = new HashSet<int>();
                var leaf = -1;
                foreach (var frameNode in stackNode.EnumerateArray())
                {
                    if (frameNode.ValueKind != JsonValueKind.Number ||
                        !frameNode.TryGetInt32(out var index) ||
                        index < 0 || index >= names.Length)
                        throw new InvalidDataException("Invalid speedscope frame index.");
                    if (unique.Add(index))
                    {
                        data[index].Inclusive += weight;
                        data[index].Samples++;
                    }
                    leaf = index;
                }
                if (leaf >= 0) data[leaf].Exclusive += weight;
            }
        }
        else
        {
            if (!profile.TryGetProperty("events", out var events) ||
                events.ValueKind != JsonValueKind.Array ||
                events.GetArrayLength() > 200000)
                throw new InvalidDataException("Invalid evented profile events.");
            if (!ReadNonNegativeNumber(profile, "startValue", out var startValue) ||
                !ReadNonNegativeNumber(profile, "endValue", out var endValue) ||
                endValue < startValue)
                throw new InvalidDataException("Invalid evented profile boundaries.");

            var stack = new List<int>();
            var previous = startValue;
            foreach (var ev in events.EnumerateArray())
            {
                if (ev.ValueKind != JsonValueKind.Object ||
                    !ReadNonNegativeNumber(ev, "at", out var time) ||
                    time < previous || time > endValue ||
                    !ev.TryGetProperty("type", out var evType) ||
                    evType.ValueKind != JsonValueKind.String ||
                    !ev.TryGetProperty("frame", out var frameNode) ||
                    frameNode.ValueKind != JsonValueKind.Number ||
                    !frameNode.TryGetInt32(out var frame) ||
                    frame < 0 || frame >= names.Length)
                    throw new InvalidDataException("Invalid evented profile event.");
                var delta = time - previous;
                if (delta > 0)
                {
                    AddInterval(data, stack, delta);
                    total += delta;
                    observationsCount++;
                }

                switch (evType.GetString())
                {
                    case "O":
                        if (stack.Count >= 1000)
                            throw new InvalidDataException("Evented call stack exceeds maximum depth.");
                        stack.Add(frame);
                        break;
                    case "C":
                        if (stack.Count == 0 || stack[^1] != frame)
                            throw new InvalidDataException("Non-LIFO evented call stack.");
                        stack.RemoveAt(stack.Count - 1);
                        break;
                    default: throw new InvalidDataException("Unknown evented frame operation.");
                }
                previous = time;
            }

            if (stack.Count != 0)
                throw new InvalidDataException("Unbalanced evented call stack.");
            if (endValue > previous)
            {
                // An interval after the final close is treated as unassigned.
                total += endValue - previous;
                observationsCount++;
            }
        }
        if (total <= 0)
            throw new InvalidDataException("No positive profile weights/durations.");

        var mappings = methods.Where(m => !string.IsNullOrWhiteSpace(m.QualifiedMember))
            .GroupBy(m => m.QualifiedMember.Replace('+', '.'),
                StringComparer.Ordinal).ToArray();
        var observations = new List<SampledFrameCost>();
        var linked = 0;
        var ambiguous = 0;
        foreach (var frame in data)
        {
            if (frame.Inclusive <= 0 && frame.Exclusive <= 0) continue;
            var normalized = frame.Name.Replace('+', '.');
            var matching = mappings.Where(group => FrameMatches(normalized, group.Key))
                .SelectMany(group => group).Select(x => x.MethodId)
                .Distinct(StringComparer.Ordinal).Take(2).ToArray();
            var status = matching.Length == 1 ? "matched_exact_qualified_name" :
                matching.Length > 1 ? "ambiguous_overload_or_type" : "unmatched";
            if (matching.Length == 1) linked++;
            if (matching.Length > 1) ambiguous++;
            observations.Add(new SampledFrameCost(frame.Name, frame.Samples,
                Math.Round(frame.Inclusive, 6), Math.Round(frame.Exclusive, 6),
                Math.Round(frame.Inclusive / total * 100, 3),
                Math.Round(frame.Exclusive / total * 100, 3),
                matching.Length == 1 ? matching[0] : null, status));
        }

        return new SampledHotspotReport(sourcePath, name, unit,
            observationsCount, Math.Round(total, 6), observations.Count,
            linked, ambiguous,
            observations.OrderByDescending(x => x.ExclusiveWeight)
                .ThenByDescending(x => x.InclusiveWeight)
                .ThenBy(x => x.FrameName, StringComparer.Ordinal)
                .Take(limit).ToArray(),
            [
                "This is a workload observation, not proof of a regression. Representative inputs, instrumentation and process settings matter.",
                "Sampled profiles use weighted stacks; evented profiles attribute observed durations between balanced open/close events. Recursion is collapsed for inclusive attribution.",
                "Method links require an unambiguous fully-qualified member name; generated methods, overloads, generic mangling and async state machines may remain unmatched.",
                "Units can be time, bytes, or sample counts. Sampled-thread-time may include waiting threads and is not necessarily on-CPU time."
            ])
        {
            ProfileType = profileType
        };
    }


    private static bool ReadNonNegativeNumber(JsonElement obj, string property, out double value)
    {
        value = 0;
        return obj.TryGetProperty(property, out var candidate) &&
            candidate.ValueKind == JsonValueKind.Number &&
            candidate.TryGetDouble(out value) &&
            double.IsFinite(value) && value >= 0;
    }

    private static void AddInterval(Accumulator[] data, List<int> stack, double weight)
    {
        var unique = new HashSet<int>();
        foreach (var frame in stack)
        {
            if (unique.Add(frame))
            {
                data[frame].Inclusive += weight;
                data[frame].Samples++;
            }
        }
        if (stack.Count > 0) data[stack[^1]].Exclusive += weight;
    }

    internal static bool FrameMatches(string frame, string fullyQualifiedMember)
    {
        // A suffix-only match can accidentally map Another.A.B.Method to
        // A.B.Method. Require a real start/boundary before the namespace.
        var start = frame.IndexOf(fullyQualifiedMember, StringComparison.Ordinal);
        while (start >= 0)
        {
            var end = start + fullyQualifiedMember.Length;
            var before = start == 0 ||
                frame[start - 1] is ' ' or '!' or ':' or '\t';
            var after = end == frame.Length ||
                frame[end] is '(' or '[' or ' ' or '<' or '\t';
            if (before && after) return true;
            start = frame.IndexOf(fullyQualifiedMember, start + 1,
                StringComparison.Ordinal);
        }
        return false;
    }
}
