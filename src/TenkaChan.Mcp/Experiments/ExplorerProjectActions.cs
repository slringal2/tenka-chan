using System.Xml;
using System.Xml.Linq;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Experiments;

public sealed record ExplorerAssembly(string Id, string ProjectId, string Project, string AssemblyPath,
    string TargetFramework, string Configuration, bool Runnable, string Explanation);
public sealed record ExplorerCompanion(string Id, string ProjectId, string Name, string ProjectPath, string Kind);
public sealed record ExplorerProjectRun(string Status, string Kind, string Project, int? ExitCode,
    string Output, string Stderr, TestRunSummary? Tests,
    IReadOnlyList<BenchmarkObservation> Benchmarks, string Note);

/// <summary>Fixed-path discovery and explicitly approved actions; never accepts caller-authored paths or commands.</summary>
public static class ExplorerProjectActions
{
    public static IReadOnlyList<ExplorerAssembly> Assemblies(SourceExplorerIndex index, WorkspaceBinaryDiscovery discovery)
    {
        var projects = index.Projects.Select(p => new ProjectSummary(p.Name, p.Path, null, p.Documents)).ToArray();
        return discovery.Discover(projects).SelectMany(a => index.Projects.Where(p => SamePath(p.Path, a.ProjectPath))
            .Select(p => new ExplorerAssembly(SourceExplorerIndex.MakeId(index.SnapshotId, p.Id, a.AssemblyPath),
                p.Id, p.Name, a.AssemblyPath, a.TargetFramework, a.Configuration, a.Runnable, a.Explanation))).ToArray();
    }

    public static IReadOnlyList<ExplorerCompanion> Companions(SourceExplorerIndex index, WorkspaceGuard guard)
    {
        var result = new List<ExplorerCompanion>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var project in index.Projects)
        {
            // Only known projects and their direct conventional companion folders; no drive-wide scan.
            var files = new List<string> { project.Path };
            foreach (var folder in new[] { "Tests", "Benchmarks" })
            {
                var directory = Path.Combine(Path.GetDirectoryName(project.Path)!, folder);
                try
                {
                    if (!Directory.Exists(directory)) continue;
                    guard.RequireDirectory(directory);
                    files.AddRange(Directory.EnumerateFiles(directory, "*.csproj").Take(20));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            foreach (var path in files)
            {
                if (!seen.Add(path)) continue;
                try
                {
                    guard.RequireFile(path, ".csproj");
                    using var reader = XmlReader.Create(path, new XmlReaderSettings
                    { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2000000 });
                    var xml = XDocument.Load(reader);
                    var packages = xml.Descendants().Where(e => e.Name.LocalName == "PackageReference")
                        .Select(e => (string?)e.Attribute("Include")).ToArray();
                    var test = xml.Descendants().Any(e => e.Name.LocalName == "IsTestProject" && e.Value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
                    test |= packages.Contains("Microsoft.NET.Test.Sdk", StringComparer.OrdinalIgnoreCase);
                    var bench = packages.Contains("BenchmarkDotNet", StringComparer.OrdinalIgnoreCase);
                    var kind = test ? "test" : bench ? "benchmark" : null;
                    if (kind is null) continue;
                    result.Add(new(SourceExplorerIndex.MakeId(index.SnapshotId, kind, path),
                        project.Id, Path.GetFileNameWithoutExtension(path), path, kind));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException) { }
            }
        }
        return result.Take(80).ToArray();
    }

    public static string EnsureOutputDirectory(string projectPath, WorkspaceGuard guard, string category)
    {
        if (category is not ("profiles" or "tests" or "benchmarks")) throw new ArgumentException("Unknown result category.");
        var project = guard.RequireFile(projectPath, ".csproj");
        var current = guard.RequireDirectory(Path.GetDirectoryName(project)!);
        foreach (var segment in new[] { ".tenka", category })
        {
            var next = Path.Combine(current, segment);
            if (File.Exists(next)) throw new IOException("結果保存先に同名のファイルがあります: " + next);
            if (Directory.Exists(next)) guard.RequireDirectory(next); // Reject existing reparse targets BEFORE writing.
            else Directory.CreateDirectory(next);
            current = guard.RequireDirectory(next);
        }
        return current;
    }

    public static async Task<ExplorerProjectRun> RunAsync(ExplorerCompanion selected, WorkspaceGuard guard,
        bool approved, bool allowRestore, CancellationToken ct)
    {
        if (!approved || !guard.TrustMsBuild || Environment.GetEnvironmentVariable("TENKA_ENABLE_LOCAL_EXEC") != "1")
            throw new UnauthorizedAccessException("ローカルのビルド・実行には設定の有効化と今回の実行への承認が必要です。");
        ct.ThrowIfCancellationRequested();
        if (selected.Kind is not ("test" or "benchmark")) throw new ArgumentException("Unsupported companion kind.");
        var project = guard.RequireFile(selected.ProjectPath, ".csproj");
        var output = EnsureOutputDirectory(project, guard, selected.Kind == "test" ? "tests" : "benchmarks");
        output = Path.Combine(output, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        guard.RequireDirectory(output);
        var info = TrustedWorkspaceBuilder.CreateStartInfo(project, null, allowRestore);
        info.ArgumentList.Clear();
        var args = selected.Kind == "test"
            ? new[] { "test", project, "-c", "Release", "--nologo", "--logger", "trx;LogFileName=results.trx", "--results-directory", output }
            : new[] { "run", "--project", project, "-c", "Release", "--no-launch-profile" };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        if (!allowRestore) info.ArgumentList.Add("--no-restore");
        if (selected.Kind == "benchmark")
            foreach (var arg in new[] { "--", "--filter", "*", "--job", "short", "--exporters", "FullJSON", "--artifacts", output })
                info.ArgumentList.Add(arg);
        WorkspaceBuildProcessResult result;
        try { result = await new DotnetWorkspaceBuildProcess().RunAsync(info, selected.Kind == "test" ? 180 : 300, ct); }
        catch (System.ComponentModel.Win32Exception ex)
        { return new("runner_unavailable", selected.Kind, selected.Name, null, "", ex.Message, null, [],
            "dotnetを起動できませんでした。SDKとPATHを確認してください。対象コードの不具合とは判断していません。"); }
        TestRunSummary? tests = null;
        var benchmarks = new List<BenchmarkObservation>();
        var status = result.Cancelled ? "cancelled" : result.TimedOut ? "timed_out" : result.ExitCode == 0 ? "unverified" : "execution_failed";
        var note = "既存プロジェクトを現在のユーザー権限でビルド・実行しました。隔離はありません。--no-restoreは任意コードやBenchmarkDotNet内部の通信を遮断しません。短いベンチマークは予備計測です。";
        try
        {
            if (selected.Kind == "test")
            {
                var file = Path.Combine(output, "results.trx");
                if (File.Exists(file))
                {
                    guard.RequireFile(file, ".trx");
                    if (new FileInfo(file).Length > 10000000) throw new InvalidDataException("TRXが上限サイズを超えています。");
                    tests = ExperimentReportParser.ReadTrx(file);
                }
                if (status == "unverified") status = tests is null ? "no_test_report" : tests.Failed > 0 ? "tests_failed"
                    : tests.Total == 0 ? "no_tests_discovered" : tests.Passed > 0 ? "tests_passed" : "unverified";
            }
            else
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 6,
                    AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false };
                foreach (var file in Directory.EnumerateFiles(output, "*-report-full.json", options).Take(20))
                {
                    guard.RequireFile(file, ".json");
                    if (new FileInfo(file).Length > 10000000) throw new InvalidDataException("ベンチマーク結果が上限サイズを超えています。");
                    benchmarks.AddRange(ExperimentReportParser.ReadBenchmarkJson(file));
                }
                if (status == "unverified" && benchmarks.Count > 0 && benchmarks.All(b => b.MeanNanoseconds is > 0 && b.SampleCount > 0))
                    status = "benchmark_completed";
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or XmlException or System.Text.Json.JsonException or ArgumentException or UnauthorizedAccessException)
        { if (status == "unverified") status = "invalid_report"; note += " レポート読取: " + ex.Message; }
        return new(status, selected.Kind, selected.Name, result.ExitCode, result.Output, result.Stderr,
            tests, benchmarks.Take(200).ToArray(), note);
    }

    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
