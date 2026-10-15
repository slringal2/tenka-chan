namespace TenkaChan.Mcp.Security;

public sealed record LargeOssSampleInfo(bool Available, string Message, string? ProjectPath = null,
    string? EntryProjectPath = null, string? FullSolutionPath = null, string? AssemblyPath = null,
    bool AssemblyExists = false, int ProjectCount = 3);

/// <summary>Fixed local paths only. Does not download, start Git/MSBuild, or expand allowed roots.</summary>
public sealed class LargeOssSampleCatalog(WorkspaceGuard guard)
{
    public LargeOssSampleInfo Find(string startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory) || !Path.IsPathFullyQualified(startDirectory))
            return new(false, "サーバーの絶対パスが必要です。");
        try
        {
            var current = new DirectoryInfo(startDirectory);
            for (var depth = 0; current is not null && depth < 8; depth++, current = current.Parent)
            {
                if (!File.Exists(Path.Combine(current.FullName, "TenkaChan.slnx")) ||
                    !File.Exists(Path.Combine(current.FullName, "src", "TenkaChan.Web", "TenkaChan.Web.csproj"))) continue;
                var root = guard.RequireDirectory(Path.Combine(current.FullName, "samples", "External", "ILSpy-10.0"));
                var solution = guard.RequireFile(Path.Combine(root, "ILSpy.Analysis.slnx"), ".slnx");
                var source = guard.RequireDirectory(Path.Combine(root, "source"));
                foreach (var project in new[] { "ICSharpCode.Decompiler", "ICSharpCode.ILSpyX", "ICSharpCode.ILSpyCmd" })
                    guard.RequireFile(Path.Combine(source, project, project + ".csproj"), ".csproj");
                var entry = Path.Combine(source, "ICSharpCode.ILSpyCmd", "ICSharpCode.ILSpyCmd.csproj");
                var full = guard.RequireFile(Path.Combine(source, "ILSpy.sln"), ".sln");
                var dll = Path.Combine(source, "ICSharpCode.ILSpyCmd", "bin", "Release", "net10.0", "ilspycmd.dll");
                var exists = File.Exists(dll);
                if (exists) dll = guard.RequireFile(dll, ".dll");
                return new(true, "ILSpyを選択できます。ここでは存在のみ確認しています。取得元・変更有無の検査はrun-large-oss.batの4で確認してください。", solution, entry, full, dll, exists);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return new(false, "ILSpyが未準備、または許可範囲外です。本体のrun-large-oss.batで1（取得）を実行してください。ビルドは2です。");
    }
}
