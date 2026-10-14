using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Experiments;

public sealed record WorkspaceAssemblyChoice(
    string Project, string ProjectPath, string AssemblyPath, string TargetFramework,
    bool Runnable, string Configuration, string Explanation);

/// <summary>
/// Discover already built assemblies from explicitly opened, allowlisted projects.
/// Never invokes MSBuild, the target assembly or shell commands.
/// </summary>
public sealed class WorkspaceBinaryDiscovery(WorkspaceGuard guard)
{
    public IReadOnlyList<WorkspaceAssemblyChoice> Discover(IReadOnlyList<ProjectSummary> projects)
    {
        var results = new List<WorkspaceAssemblyChoice>();
        foreach (var project in projects.Take(80))
        {
            if (project.Path is null || !project.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                continue;
            var file = guard.RequireFile(project.Path, ".csproj");
            var projectDir = Path.GetDirectoryName(file)!;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { Path.GetFileNameWithoutExtension(file) };
            // AssemblyName is an optional literal override. Do not evaluate properties or imports.
            try
            {
                var xml = XDocument.Load(file);
                foreach (var value in xml.Descendants().Where(x => x.Name.LocalName == "AssemblyName")
                    .Select(x => x.Value.Trim()))
                    if (value.Length is > 0 and < 120 && !value.Contains('$') &&
                        value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                        names.Add(value);
            }
            catch (System.Xml.XmlException) { }
            var bin = Path.Combine(projectDir, "bin");
            if (!Directory.Exists(bin)) continue;
            guard.RequireDirectory(bin);
            foreach (var config in new[] { "Release", "Debug" })
            {
                var dir = Path.Combine(bin, config);
                if (!Directory.Exists(dir)) continue;
                guard.RequireDirectory(dir);
                foreach (var frameworkDir in Directory.EnumerateDirectories(dir).Take(80))
                {
                    guard.RequireDirectory(frameworkDir);
                    if (!SupportedFramework(Path.GetFileName(frameworkDir))) continue;
                    var candidates = new List<string> { frameworkDir };
                    // Runtime identifier outputs, e.g. net8.0/win-x64, may be one level deeper.
                    candidates.AddRange(Directory.EnumerateDirectories(frameworkDir).Take(24)
                        .Where(path => !Path.GetFileName(path).StartsWith('.')));
                    foreach (var folder in candidates)
                    {
                        guard.RequireDirectory(folder);
                        foreach (var name in names)
                        {
                            var dll = Path.Combine(folder, name + ".dll");
                            if (!File.Exists(dll)) continue;
                            try
                            {
                                guard.RequireFile(dll, ".dll");
                                using var fs = File.OpenRead(dll);
                                using var pe = new PEReader(fs);
                                if (!pe.HasMetadata || !pe.GetMetadataReader().IsAssembly) continue;
                                var runnable = File.Exists(Path.ChangeExtension(dll, ".runtimeconfig.json"))
                                    && pe.PEHeaders.CorHeader is { EntryPointTokenOrRelativeVirtualAddress: not 0 };
                                if (runnable)
                                    guard.RequireFile(Path.ChangeExtension(dll, ".runtimeconfig.json"), ".json");
                                results.Add(new WorkspaceAssemblyChoice(project.Name, file, dll,
                                    Path.GetFileName(frameworkDir), runnable, config,
                                    runnable ? "Existing runnable managed DLL" :
                                    "Existing managed library; IL inspection works, but it cannot be launched directly."));
                            }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                or InvalidDataException or BadImageFormatException) { }
                        }
                    }
                }
            }
        }
        return results.GroupBy(x => x.AssemblyPath,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Select(x => x.First())
            .OrderByDescending(x => x.Runnable)
            .ThenBy(x => x.Configuration == "Release" ? 0 : 1)
            .ThenBy(x => x.Project, StringComparer.Ordinal)
            .ThenBy(x => x.AssemblyPath, StringComparer.Ordinal)
            .Take(100).ToArray();
    }

    public WorkspaceAssemblyChoice RequireBuiltProject(string projectFile, string framework)
    {
        var safeProject = guard.RequireFile(projectFile, ".csproj");
        var project = new ProjectSummary(Path.GetFileNameWithoutExtension(safeProject),
            safeProject, framework, 0);
        var matches = Discover([project])
            .Where(x => string.Equals(x.TargetFramework, framework, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.FirstOrDefault(x => x.Configuration == "Release")
            ?? matches.FirstOrDefault()
            ?? throw new FileNotFoundException(
                "No built managed assembly found for this project and framework. Build it in Release first (dotnet build -c Release).");
    }

    private static bool SupportedFramework(string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(name, @"^net(?<major>\d+)\.\d+(?:[-.].*)?$");
        return match.Success && int.TryParse(match.Groups["major"].Value, out var major) && major >= 8;
    }
}
