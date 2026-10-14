namespace TenkaChan.Mcp.Analysis;

public sealed partial class RoslynReviewService
{
    private SourceExplorerIndex? _explorerIndex;

    public void ClearExplorerMeasurement() => _hotspotProfile = null;

    public async Task<SourceExplorerIndex> ExplorerAsync(CancellationToken ct = default)
    {
        await _openLock.WaitAsync(ct);
        try
        {
            EnsureOpen();
            if (_explorerIndex?.SnapshotId != CurrentSnapshotId)
                _explorerIndex = await SourceExplorerIndex.CreateAsync(CurrentSnapshotId, _solution!,
                    _methods.Values.Select(e => new ExplorerMethodSeed(e.Summary, e.Document, e.Syntax, e.Symbol)), ct);
            return _explorerIndex;
        }
        finally { _openLock.Release(); }
    }
}
