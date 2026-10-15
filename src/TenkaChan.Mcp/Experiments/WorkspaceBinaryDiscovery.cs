using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Experiments;

public sealed record WorkspaceJitMethodChoice(string MethodId, string Project,
    string Signature, string JitFilter);

public sealed record WorkspaceAssemblyChoice(
    string Project, string ProjectPath, string AssemblyPath, string TargetFramework,
    bool Runnable, string Configuration, string Explanation)
{
    public string? RuntimeIdentifier { get; init; }
}

/// <summary>
/// Read-only discovery. Output paths are candidates, not proof of source/build identity.
/// Only literal project properties and a small, explicit set of path substitutions
/// are supported; no MSBuild evaluation, imports, shell or assembly execution occurs.
/// </summary>
public sealed class WorkspaceBinaryDiscovery(WorkspaceGuard guard)
{
    private static readonly StringComparer Paths = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private sealed record Folder(string Path, string? Framework, string Configuration,
        string? Rid, string Origin);

    public IReadOnlyList<WorkspaceAssemblyChoice> Discover(IReadOnlyList<ProjectSummary> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        var results = new List<WorkspaceAssemblyChoice>();
        foreach (var project in projects.OrderBy(x => x.Path, Paths).Take(80))
        {
            try { DiscoverProject(project, results); }
            catch (Exception ex) when (IsDiscoveryFailure(ex))
            {
                // A removed, inaccessible or disallowed project must not hide other projects.
            }
        }
        return results.GroupBy(x => x.AssemblyPath, Paths).Select(g => g.First())
            .OrderByDescending(x => x.Runnable)
            .ThenBy(x => ConfigurationRank(x.Configuration))
            .ThenBy(x => HostRank(x.RuntimeIdentifier))
            .ThenBy(x => x.Project, StringComparer.Ordinal)
            .ThenBy(x => x.AssemblyPath, Paths).Take(100).ToArray();
    }

    private void DiscoverProject(ProjectSummary project, List<WorkspaceAssemblyChoice> results)
    {
        if (project.Path is null || !project.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) return;
        var file = guard.RequireFile(project.Path, ".csproj");
        var root = Path.GetDirectoryName(file)!;
        var names = new HashSet<string>(Paths) { Path.GetFileNameWithoutExtension(file) };
        var frameworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (InspectableFramework(project.TargetFramework)) frameworks.Add(project.TargetFramework!);
        XDocument? xml = null;
        try
        {
            using var reader = XmlReader.Create(file, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
            xml = XDocument.Load(reader);
            foreach (var value in Values(xml, "AssemblyName").Take(32))
                if (value.Length is > 0 and < 120 && !value.Contains('$') &&
                    value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                    !value.Contains('/') && !value.Contains('\\')) names.Add(value);
            foreach (var value in Values(xml, "TargetFramework").Concat(Values(xml, "TargetFrameworks"))
                .SelectMany(x => x.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).Take(32))
                if (InspectableFramework(value)) frameworks.Add(value);
        }
        catch (XmlException) { /* Conventional paths remain available; DTDs are never expanded. */ }

        var folders = new List<Folder>();
        foreach (var config in new[] { "Release", "Debug" })
        {
            foreach (var frameworkDir in Children(Path.Combine(root, "bin", config), 80))
                if (InspectableFramework(Path.GetFileName(frameworkDir)))
                {
                    var tfm = Path.GetFileName(frameworkDir);
                    frameworks.Add(tfm);
                    AddOutput(folders, frameworkDir, tfm, config, "conventional output");
                }
        }
        if (xml is not null)
        {
            // Do not interpret Conditions or imports: these are explicitly labelled path hints.
            // In particular, a shared OutDir is not falsely labelled a Release build.
            var tfms = frameworks.OrderBy(x => x, StringComparer.Ordinal).Take(16).ToArray();
            foreach (var property in new[] { "OutputPath", "OutDir", "BaseOutputPath" })
            foreach (var value in Values(xml, property).Take(16))
            foreach (var config in new[] { "Release", "Debug" })
            foreach (var tfm in tfms.Length == 0 ? new[] { "" } : tfms)
            {
                var expanded = value.Replace("$(Configuration)", config, StringComparison.OrdinalIgnoreCase)
                    .Replace("$(TargetFramework)", tfm, StringComparison.OrdinalIgnoreCase)
                    .Replace("$(MSBuildProjectDirectory)", root, StringComparison.OrdinalIgnoreCase);
                if (expanded.Length is 0 or > 2048 || expanded.IndexOfAny(['$', '@', '%', '*', '?', ';', '\0']) >= 0) continue;
                try
                {
                    var path = Path.GetFullPath(expanded.Replace('\\', Path.DirectorySeparatorChar), root);
                    if (property == "BaseOutputPath") path = Path.Combine(path, config);
                    var label = InferConfiguration(path);
                    if (tfm.Length > 0) AddOutput(folders, Path.Combine(path, tfm), tfm, label, property + " hint");
                    AddOutput(folders, path, tfm.Length == 0 ? null : tfm, label, property + " hint");
                }
                catch (Exception ex) when (IsDiscoveryFailure(ex)) { }
            }
        }

        // Preserve distinct framework hints until metadata has validated them.
        foreach (var folder in folders.Distinct().Take(512))
        foreach (var name in names.OrderBy(x => x, Paths))
        {
            try
            {
                var dll = guard.RequireFile(Path.Combine(folder.Path, name + ".dll"), ".dll");
                using var stream = File.OpenRead(dll);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata) continue;
                var metadata = pe.GetMetadataReader();
                if (!metadata.IsAssembly) continue;
                var actual = ReadFramework(metadata);
                if (!InspectableFramework(actual)) continue;
                if (folder.Framework is { } hint &&
                    !string.Equals(hint.Split('-')[0], actual, StringComparison.OrdinalIgnoreCase)) continue;
                var tfm = folder.Framework ?? actual!;
                var runnable = SupportedFramework(actual) && HostRank(folder.Rid) < 2 && pe.PEHeaders.CorHeader is { EntryPointTokenOrRelativeVirtualAddress: not 0 }
                    && HasRuntimeConfig(dll, actual!);
                results.Add(new WorkspaceAssemblyChoice(project.Name, file, dll, tfm, runnable,
                    folder.Configuration, (runnable ? "Existing runnable managed DLL" :
                    IsNetStandard(actual) ? ".NET Standard library: metadata/IL inspection only; choose a compatible .NET application to execute it" :
                    "Managed library or incompatible/unconfigured app; cannot launch directly on this host") +
                    $"; {folder.Origin}. Base framework checked in PE metadata; configuration/platform are path hints, not verified build identity.")
                    { RuntimeIdentifier = folder.Rid });
            }
            catch (Exception ex) when (IsDiscoveryFailure(ex)) { }
        }
    }

    public WorkspaceAssemblyChoice RequireBuiltProject(string projectFile, string framework)
    {
        var safe = guard.RequireFile(projectFile, ".csproj");
        if (!SupportedFramework(framework)) throw new ArgumentException("A .NET 8+ target framework is required.");
        var choices = Discover([new ProjectSummary(Path.GetFileNameWithoutExtension(safe), safe, framework, 0)])
            .Where(x => string.Equals(x.TargetFramework, framework, StringComparison.OrdinalIgnoreCase)
                && HostRank(x.RuntimeIdentifier) < 2)
            .OrderBy(x => ConfigurationRank(x.Configuration)).ThenBy(x => HostRank(x.RuntimeIdentifier)).ToArray();
        if (choices.Length == 0) throw new FileNotFoundException(
            "No compatible built .NET 8+ assembly found. Build the selected project/framework in Release first. " +
            "Imported or computed output paths are not evaluated by read-only discovery.");
        var best = choices[0];
        if (choices.Skip(1).Any(x => ConfigurationRank(x.Configuration) == ConfigurationRank(best.Configuration)
            && HostRank(x.RuntimeIdentifier) == HostRank(best.RuntimeIdentifier)))
            throw new InvalidOperationException(
                "Multiple equally preferred build outputs were found; refusing to execute an arbitrary DLL. " +
                "Clean obsolete outputs and rebuild the intended configuration/framework.");
        return best;
    }

    private void AddOutput(List<Folder> folders, string path, string? tfm, string config, string origin)
    {
        if (folders.Count >= 512) return;
        try { guard.RequireDirectory(path); }
        catch (Exception ex) when (IsDiscoveryFailure(ex)) { return; }
        folders.Add(new Folder(path, tfm, config, null, origin));
        foreach (var child in Children(path, 32))
        {
            var rid = Path.GetFileName(child);
            // Never descend into ref/refint/publish or arbitrary directories.
            if (folders.Count >= 512) break;
            if (Regex.IsMatch(rid, @"^(win(?:\d+)?|linux(?:-musl)?|osx)(?:\.\d+(?:\.\d+)*)?-(x64|x86|arm64|arm)$",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                folders.Add(new Folder(child, tfm, config, rid, origin));
        }
    }

    private string[] Children(string path, int limit)
    {
        try
        {
            guard.RequireDirectory(path);
            var children = Directory.EnumerateDirectories(path).Take(limit + 1).ToArray();
            // Bounded and deterministic: do not pick an arbitrary subset of an oversized directory.
            return children.Length > limit ? [] : children.OrderBy(x => x, Paths).ToArray();
        }
        catch (Exception ex) when (IsDiscoveryFailure(ex)) { return []; }
    }

    private bool HasRuntimeConfig(string dll, string framework)
    {
        try
        {
            var file = guard.RequireFile(Path.ChangeExtension(dll, ".runtimeconfig.json"), ".json");
            using var stream = File.OpenRead(file);
            if (stream.Length > 1_000_000) return false;
            using var json = JsonDocument.Parse(stream);
            return json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("runtimeOptions", out var options)
                && options.ValueKind == JsonValueKind.Object
                && options.TryGetProperty("tfm", out var tfm) && tfm.ValueKind == JsonValueKind.String
                && string.Equals(tfm.GetString()?.Split('-')[0], framework, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (IsDiscoveryFailure(ex)) { return false; }
    }

    private static string? ReadFramework(MetadataReader reader)
    {
        foreach (var handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            var constructor = attribute.Constructor;
            EntityHandle type = constructor.Kind switch
            {
                HandleKind.MemberReference => reader.GetMemberReference((MemberReferenceHandle)constructor).Parent,
                HandleKind.MethodDefinition => reader.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
                _ => default
            };
            string? name = null, ns = null;
            if (type.Kind == HandleKind.TypeReference)
            {
                var t = reader.GetTypeReference((TypeReferenceHandle)type);
                name = reader.GetString(t.Name); ns = reader.GetString(t.Namespace);
            }
            else if (type.Kind == HandleKind.TypeDefinition)
            {
                var t = reader.GetTypeDefinition((TypeDefinitionHandle)type);
                name = reader.GetString(t.Name); ns = reader.GetString(t.Namespace);
            }
            if (name != "TargetFrameworkAttribute" || ns != "System.Runtime.Versioning") continue;
            var blob = reader.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1) return null;
            var value = blob.ReadSerializedString();
            if (value is null || value.Length > 256) return null;
            var match = Regex.Match(value, @"^\.(?<family>NETCoreApp|NETStandard),Version=v(?<version>\d+\.\d+)$",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            return match.Success ? (match.Groups["family"].Value == "NETStandard" ? "netstandard" : "net") + match.Groups["version"].Value : null;
        }
        return null;
    }

    private static IEnumerable<string> Values(XDocument xml, string name) =>
        xml.Descendants().Where(x => x.Name.LocalName == name).Select(x => x.Value.Trim());
    // Discovery is broader than execution. Do NOT reuse this predicate in a host runner.
    private static bool IsNetStandard(string? value) => value is "netstandard2.0" or "netstandard2.1";
    private static bool InspectableFramework(string? value) => SupportedFramework(value) || IsNetStandard(value);
    private static bool SupportedFramework(string? value) => value is { Length: > 0 and < 120 }
        && Regex.IsMatch(value, @"^net(?:[89]|[1-9]\d+)\.\d+(?:-[A-Za-z0-9][A-Za-z0-9.-]*)?$",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static string InferConfiguration(string path) =>
        path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar])
            .Select(x => x.Equals("Release", StringComparison.OrdinalIgnoreCase) ? "Release"
                : x.Equals("Debug", StringComparison.OrdinalIgnoreCase) ? "Debug" : null)
            .LastOrDefault(x => x is not null) ?? "Unknown";
    private static int ConfigurationRank(string value) => value == "Release" ? 0 : value == "Debug" ? 2 : 1;
    private static int HostRank(string? rid)
    {
        if (rid is null) return 0;
        var prefix = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        return rid.StartsWith(prefix, StringComparison.Ordinal) && rid.EndsWith("-" + arch, StringComparison.Ordinal) ? 1 : 2;
    }
    private static bool IsDiscoveryFailure(Exception ex) => ex is IOException or UnauthorizedAccessException
        or ArgumentException or BadImageFormatException or InvalidDataException or JsonException;
}
