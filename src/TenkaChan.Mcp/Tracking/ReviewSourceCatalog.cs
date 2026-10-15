using System.Text;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Tracking;

public sealed record ReviewSourceInput(string ProjectId,string Path,string Hash,Encoding Encoding);
public sealed class ReviewSourceCatalog(string snapshot,IReadOnlyList<ReviewTarget> targets,
    IReadOnlyList<ReviewSourceInput> inputs,IReadOnlyDictionary<string,HashSet<string>> dependencies,bool limited)
{
    public string SnapshotId => snapshot;
    public IReadOnlyList<ReviewTarget> Targets => targets;
    public bool Limited => limited;
    private DateTimeOffset _lastRead;
    private HashSet<string>? _stale;
    public ReviewTarget Require(string id) => targets.FirstOrDefault(t=>t.Id==id)
        ?? throw new KeyNotFoundException("現在のプロジェクトにこの対象はありません。確認一覧を更新してください。");
    public async Task<HashSet<string>> StaleProjectsAsync(WorkspaceGuard guard,bool force,CancellationToken ct)
    {
        // DashboardState serializes callers. Marking always performs an uncached content check.
        if(!force&&_stale is not null&&DateTimeOffset.UtcNow-_lastRead<TimeSpan.FromSeconds(3))return _stale;
        var changed=new HashSet<string>(StringComparer.Ordinal);
        foreach(var input in inputs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var file=guard.RequireFile(input.Path,Path.GetExtension(input.Path));
                if(ReviewProgress.Hash(await ReadTextAsync(file,input.Encoding,ct))!=input.Hash)changed.Add(input.ProjectId);
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException)
            {changed.Add(input.ProjectId);}
        }
        _stale=dependencies.Where(p=>p.Value.Overlaps(changed)).Select(p=>p.Key).ToHashSet(StringComparer.Ordinal);
        _lastRead=DateTimeOffset.UtcNow;return _stale;
    }
    public static async Task<string> ReadTextAsync(string path,Encoding encoding,CancellationToken ct)
    {
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        if(file.Length>16000000)throw new IOException("Review input file exceeds budget.");
        using var reader=new StreamReader(file,encoding,detectEncodingFromByteOrderMarks:true);
        var text=new StringBuilder();var buffer=new char[8192];int read;
        while((read=await reader.ReadAsync(buffer.AsMemory(),ct))>0)
        {
            if(text.Length+read>4000000)throw new IOException("Review input text exceeds budget.");
            text.Append(buffer,0,read);
        }
        return text.ToString();
    }
}
