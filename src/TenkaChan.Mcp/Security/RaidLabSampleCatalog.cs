namespace TenkaChan.Mcp.Security;

public sealed record RaidLabSampleInfo(bool Available, string Message, string? ProjectPath = null,
    string? EntryProjectPath = null, string? AssemblyPath = null, bool AssemblyExists = false, int ProjectCount = 0);

/// <summary>Read-only, fixed sample discovery. No MSBuild, target execution, directory creation or permission expansion.</summary>
public sealed class RaidLabSampleCatalog(WorkspaceGuard guard)
{
    public RaidLabSampleInfo Find(string startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory) || !Path.IsPathFullyQualified(startDirectory))
            return new(false, "絶対パスのサーバールートが必要です。");
        try
        {
            var current = new DirectoryInfo(startDirectory);
            for (var depth = 0; current is not null && depth < 8; depth++, current = current.Parent)
            {
                if (!File.Exists(Path.Combine(current.FullName, "TenkaChan.slnx")) ||
                    !File.Exists(Path.Combine(current.FullName, "src", "TenkaChan.Web", "TenkaChan.Web.csproj"))) continue;
                var sample = guard.RequireDirectory(Path.Combine(current.FullName, "samples", "Tenka.Sample.RaidLab"));
                var solution = guard.RequireFile(Path.Combine(sample, "Tenka.Sample.RaidLab.sln"), ".sln");
                foreach (var name in new[] { "Domain", "Infrastructure", "Application", "Host", "Tests", "Benchmarks" })
                    guard.RequireFile(Path.Combine(sample, name, "Tenka.Sample.RaidLab." + name + ".csproj"), ".csproj");
                var entry = Path.Combine(sample, "Host", "Tenka.Sample.RaidLab.Host.csproj");
                var dll = Path.Combine(sample, "Host", "bin", "Release", "net8.0", "Tenka.Sample.RaidLab.Host.dll");
                var exists = File.Exists(dll);
                if (exists) dll = guard.RequireFile(dll, ".dll");
                return new(true, "RaidLabの6プロジェクトを選択できます。読み込み・ビルド・実行はまだ行っていません。", solution, entry, dll, exists, 6);
            }
            return new(false, "RaidLabを含むソースチェックアウトを使用してください。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { return new(false, "RaidLabが未配置、または許可フォルダーの外にあります。本体を更新し、setup.batの許可範囲を確認してください。"); }
    }
}
