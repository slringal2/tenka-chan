namespace TenkaChan.Mcp.Tracking;

/// <summary>Reuse a bounded line diff across pages, keyed by both complete normalized source hashes.</summary>
public sealed class ReviewDiffCache
{
    private const int MaximumEntries = 8;
    private const long MaximumCharacters = 1600000;
    private readonly object _gate = new();
    private readonly Dictionary<string, (ReviewDiffDocument Document, long Characters)> _entries = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private long _characters, _computations;
    public long Computations { get { lock (_gate) return _computations; } }

    public ReviewDiff Compare(string before, string after, int offset = 0, int limit = 150)
    {
        ReviewSourceDiff.ValidatePage(offset, limit);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Length > ReviewProgress.MaxSourceCharacters || after.Length > ReviewProgress.MaxSourceCharacters)
            throw new ArgumentException("Diff source exceeds the bounded checkpoint size.");
        var key = ReviewProgress.Hash(before) + ":" + ReviewProgress.Hash(after);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var cached)) return cached.Document.Page(offset, limit);
            var document = ReviewSourceDiff.Build(before, after);
            _computations++;
            var size = document.Rows.Sum(r => (long)r.Text.Length);
            while (_order.Count > 0 && (_entries.Count >= MaximumEntries || _characters + size > MaximumCharacters))
            {
                var oldest = _order.Dequeue();
                _characters -= _entries[oldest].Characters;
                _entries.Remove(oldest);
            }
            if (size <= MaximumCharacters)
            {
                _entries.Add(key, (document, size)); _order.Enqueue(key); _characters += size;
            }
            return document.Page(offset, limit);
        }
    }
}
