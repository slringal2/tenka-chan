namespace TenkaChan.Mcp.Analysis;

/// <summary>
/// Match qualified names at explicit frame-label boundaries. This is a name
/// association, not a module/MVID/build identity. Overloads stay ambiguous.
/// A trie avoids scanning every Roslyn method for every profiler frame.
/// </summary>
internal sealed class ProfileFrameMatcher
{
    private sealed class Node
    {
        public Dictionary<char, Node> Children { get; } = [];
        public HashSet<string> MethodIds { get; } = new(StringComparer.Ordinal);
    }
    private readonly Node _root = new();

    public ProfileFrameMatcher(IReadOnlyList<ProfileMethodIdentity> methods)
    {
        foreach (var method in methods)
        {
            if (string.IsNullOrWhiteSpace(method.QualifiedMember)) continue;
            var name = method.QualifiedMember.Replace('+', '.');
            var node = _root;
            foreach (var character in name)
            {
                if (!node.Children.TryGetValue(character, out var child))
                    node.Children.Add(character, child = new Node());
                node = child;
            }
            node.MethodIds.Add(method.MethodId);
        }
    }

    public string[] Match(string label)
    {
        var frame = label.Replace('+', '.');
        var matches = new HashSet<string>(StringComparer.Ordinal);
        for (int start = 0; start < frame.Length; start++)
        {
            if (start > 0 && frame[start - 1] is not (' ' or '!' or ':' or '\t')) continue;
            var node = _root;
            for (int at = start; at < frame.Length; at++)
            {
                if (!node.Children.TryGetValue(frame[at], out var child)) break;
                node = child;
                int end = at + 1;
                if (end != frame.Length && frame[end] is not ('(' or '[' or ' ' or '<' or '\t')) continue;
                foreach (var method in node.MethodIds)
                {
                    matches.Add(method);
                    if (matches.Count > 1) return matches.Order(StringComparer.Ordinal).ToArray();
                }
            }
        }
        return matches.ToArray();
    }
}
