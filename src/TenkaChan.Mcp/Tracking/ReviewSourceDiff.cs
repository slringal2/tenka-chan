namespace TenkaChan.Mcp.Tracking;

public sealed record ReviewDiffLine(string Kind, int? OldLine, int? NewLine, string Text);
public sealed record ReviewDiff(string Mode, int Added, int Removed, int TotalRows, int Offset,
    IReadOnlyList<ReviewDiffLine> Rows, bool HasMore, string Note);

/// <summary>Bounded line comparison; large changes fall back to an exact but nonminimal replacement.
/// No shell/git invocation; the input is the stored checkpoint and loaded source.</summary>
public static class ReviewSourceDiff
{
    public static ReviewDiff Compare(string before, string after, int offset = 0, int limit = 150)
    {
        if (offset < 0 || limit is < 1 or > 500) throw new ArgumentException("Invalid diff page.");
        if (before.Length > ReviewProgress.MaxSourceCharacters || after.Length > ReviewProgress.MaxSourceCharacters)
            throw new ArgumentException("Diff source exceeds the bounded checkpoint size.");
        var a = Lines(before); var b = Lines(after);
        if (a.Length > 20000 || b.Length > 20000) throw new ArgumentException("Diff exceeds 20,000 lines.");
        int prefix = 0, suffix = 0;
        while (prefix < Math.Min(a.Length,b.Length) && a[prefix] == b[prefix]) prefix++;
        while (suffix < Math.Min(a.Length,b.Length) - prefix && a[^(suffix+1)] == b[^(suffix+1)]) suffix++;
        int n = a.Length-prefix-suffix, m = b.Length-prefix-suffix;
        var rows = new List<ReviewDiffLine>();
        for (var i = Math.Max(0,prefix-3); i < prefix; i++) rows.Add(new("context",i+1,i+1,a[i]));
        int added=0,removed=0; bool coarse=(long)(n+1)*(m+1)>1000000;
        if (coarse)
        {
            for (var i=0;i<n;i++) rows.Add(new("remove",prefix+i+1,null,a[prefix+i]));
            for (var j=0;j<m;j++) rows.Add(new("add",null,prefix+j+1,b[prefix+j]));
            removed=n;added=m;
        }
        else
        {
            var lengths = new int[(n+1)*(m+1)];
            for (var i=n-1;i>=0;i--) for (var j=m-1;j>=0;j--)
                lengths[i*(m+1)+j]=a[prefix+i]==b[prefix+j] ? 1+lengths[(i+1)*(m+1)+j+1]
                    : Math.Max(lengths[(i+1)*(m+1)+j],lengths[i*(m+1)+j+1]);
            int x=0,y=0;
            while (x<n || y<m)
            {
                if (x<n && y<m && a[prefix+x]==b[prefix+y])
                { rows.Add(new("context",prefix+x+1,prefix+y+1,a[prefix+x]));x++;y++; }
                else if (y<m && (x==n || lengths[x*(m+1)+y+1]>lengths[(x+1)*(m+1)+y]))
                { rows.Add(new("add",null,prefix+y+1,b[prefix+y]));y++;added++; }
                else { rows.Add(new("remove",prefix+x+1,null,a[prefix+x]));x++;removed++; }
            }
        }
        for (var i=0;i<Math.Min(3,suffix);i++) rows.Add(new("context",a.Length-suffix+i+1,b.Length-suffix+i+1,a[a.Length-suffix+i]));
        var visible = new List<ReviewDiffLine>();
        if (added+removed>0)
        {
            var keep=new bool[rows.Count];
            for (var i=0;i<rows.Count;i++) if (rows[i].Kind!="context")
                for (var k=Math.Max(0,i-3);k<=Math.Min(rows.Count-1,i+3);k++) keep[k]=true;
            for (var i=0;i<rows.Count;i++)
            {
                if (keep[i]) { visible.Add(rows[i]);continue; }
                var start=i;while (i+1<rows.Count&&!keep[i+1]) i++;
                visible.Add(new("skip",null,null,$"変更のない{i-start+1}行を省略"));
            }
        }
        return new(coarse?"bounded_replacement":"line_lcs",added,removed,visible.Count,offset,
            visible.Skip(offset).Take(limit).ToArray(),(long)offset+limit<visible.Count,
            coarse?"大きな変更は区間全体を置換表示しています。追加・削除数は最小差分ではありません。":"確認時点と読み込み済みソースの行差分です。Gitコミット差分や意味的な等価性の判定ではありません。");
    }
    private static string[] Lines(string value) => value.Length==0 ? [] : ReviewProgress.Normalize(value).Split('\n');
}
