using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Experiments;

/// <summary>
/// A bounded fingerprint of a companion's declared source inputs. Does not claim
/// to capture imported MSBuild logic, linked files, compiler options or the binary
/// that was actually run. Used only to reject obviously changed benchmark workloads.
/// </summary>
public sealed record CompanionBenchmarkWorkload(string ProjectPath, string SourceSha256,
    string ProjectSha256, string RunnerSha256);

public static class CompanionBenchmarkWorkloads
{
    private static readonly StringComparer Paths = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private const int MaxSourceFiles = 128;
    private const int MaxProjectReferences = 64;
    private const long MaxSourceFileBytes = 2_000_000;

    public static CompanionBenchmarkWorkload Capture(WorkspaceGuard guard, string projectPath)
    {
        var project = guard.RequireFile(projectPath, ".csproj");
        var root = guard.RequireDirectory(Path.GetDirectoryName(project)!);
        var sources = new List<string>();
        CollectSources(guard, root, sources, 0);
        if (sources.Count == 0)
            throw new InvalidOperationException("companion_workload_missing: No local .cs benchmark source was found.");
        var sourceLines = sources.OrderBy(x => x, Paths)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/') + ":" + HashFile(path)).ToArray();
        var projectLines = new List<string> { "project:" + HashFile(project) };
        // Directory.Build.props/targets are conventional inputs, but arbitrary imported
        // projects and dynamically generated source remain outside this fingerprint.
        var current = root;
        for (var depth = 0; current is not null && depth < 12; depth++, current = Path.GetDirectoryName(current))
        {
            try { guard.RequireDirectory(current); }
            catch (UnauthorizedAccessException) { break; }
            foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets" })
            {
                var file = Path.Combine(current, name);
                if (!File.Exists(file)) continue;
                guard.RequireFile(file, Path.GetExtension(file));
                projectLines.Add(name + "@" + depth + ":" + HashFile(file));
            }
        }
        var runner = sources.FirstOrDefault(p => Path.GetFileName(p) == "Program.cs");
        var sourceSha = HashText(string.Join("\n", sourceLines));
        return new CompanionBenchmarkWorkload(project, sourceSha,
            HashText(string.Join("\n", projectLines)), runner is null ? sourceSha : HashFile(runner));
    }

    /// <summary>
    /// Requires a literal project-reference chain in the companion's own project files.
    /// This is a scope check, not proof that a benchmark calls the target method.
    /// </summary>
    public static bool DeclaresReferenceTo(WorkspaceGuard guard, string companion, string target)
    {
        var start = guard.RequireFile(companion, ".csproj");
        var wanted = guard.RequireFile(target, ".csproj");
        var seen = new HashSet<string>(Paths);
        var queue = new Queue<string>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var project = queue.Dequeue();
            if (!seen.Add(project)) continue;
            if (seen.Count > MaxProjectReferences)
                throw new InvalidOperationException("companion_reference_limit: Too many project references.");
            foreach (var reference in ProjectReferences(project))
            {
                // Evaluating arbitrary MSBuild expressions here would execute no code but
                // could silently select a different project. Accept literal paths only.
                if (reference.IndexOfAny(['$', '@', '%', '*', '?', ';', '\0']) >= 0) continue;
                string file;
                try { file = guard.RequireFile(Path.GetFullPath(
                    reference.Replace('\\', Path.DirectorySeparatorChar),
                    Path.GetDirectoryName(project)!), ".csproj"); }
                catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
                { continue; }
                if (Paths.Equals(file, wanted)) return true;
                if (!seen.Contains(file)) queue.Enqueue(file);
            }
        }
        return false;
    }

    private static IEnumerable<string> ProjectReferences(string project)
    {
        using var reader = XmlReader.Create(project, new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
        var xml = XDocument.Load(reader);
        return xml.Descendants().Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => ((string?)e.Attribute("Include") ?? "").Trim())
            .Where(x => x.Length is > 0 and <= 1024).Take(MaxProjectReferences + 1).ToArray();
    }

    private static void CollectSources(WorkspaceGuard guard, string directory, List<string> files, int depth)
    {
        if (depth > 8) throw new InvalidOperationException("companion_source_limit: Directory nesting exceeds the scan limit.");
        var current = guard.RequireDirectory(directory);
        foreach (var file in Directory.EnumerateFiles(current, "*.cs", SearchOption.TopDirectoryOnly).OrderBy(x => x, Paths))
        {
            files.Add(guard.RequireFile(file, ".cs"));
            if (files.Count > MaxSourceFiles)
                throw new InvalidOperationException("companion_source_limit: Too many benchmark source files.");
        }
        var subdirs = Directory.EnumerateDirectories(current).Take(65).ToArray();
        if (subdirs.Length > 64)
            throw new InvalidOperationException("companion_source_limit: Too many source subdirectories.");
        foreach (var child in subdirs.OrderBy(x => x, Paths))
        {
            if (Path.GetFileName(child) is "bin" or "obj" or ".tenka" or ".git") continue;
            CollectSources(guard, child, files, depth + 1);
        }
    }

    private static string HashFile(string path)
    {
        if (new FileInfo(path).Length > MaxSourceFileBytes)
            throw new InvalidOperationException("companion_source_limit: Benchmark source file is too large.");
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }
    private static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
