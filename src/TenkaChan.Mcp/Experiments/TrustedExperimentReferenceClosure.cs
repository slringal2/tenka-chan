using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Experiments;

/// <summary>
/// Resolves already-built, colocated managed dependencies through PE assembly references.
/// No MSBuild evaluation, project-reference build, assembly loading or process execution.
/// In particular, a generated host experiment must not rebuild the caller's solution.
/// </summary>
internal static class TrustedExperimentReferenceClosure
{
    private const int MaxCandidates = 160;
    private const long MaxAssemblyBytes = 64L * 1024 * 1024;
    private static readonly StringComparer Names = StringComparer.OrdinalIgnoreCase;
    private static readonly StringComparer Paths = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed record LocalAssembly(string Name, string Path, IReadOnlyList<string> References);

    internal static IReadOnlyList<(string Name, string Path)> Resolve(WorkspaceGuard guard, string subjectDll)
    {
        var subject = guard.RequireFile(subjectDll, ".dll");
        var directory = guard.RequireDirectory(Path.GetDirectoryName(subject)!);
        // Reject an oversized output folder instead of arbitrarily dropping required
        // references based on filesystem enumeration order.
        var paths = Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
            .Take(MaxCandidates + 1).ToArray();
        if (paths.Length > MaxCandidates)
            throw new InvalidOperationException("experiment_reference_limit: Too many DLLs in the selected build output; choose a clean build output.");
        var local = new Dictionary<string, LocalAssembly>(Names);
        LocalAssembly? root = null;
        foreach (var path in paths.OrderBy(x => x, Paths))
        {
            var safe = guard.RequireFile(path, ".dll");
            var info = new FileInfo(safe);
            if (info.Length > MaxAssemblyBytes)
                throw new InvalidOperationException("experiment_reference_limit: A build output DLL is too large to inspect safely.");
            var item = ReadMetadata(safe);
            if (item is null) continue; // e.g. a native dependency; never load it here.
            if (local.ContainsKey(item.Name))
                throw new InvalidOperationException("experiment_reference_ambiguous: Multiple DLLs declare the same assembly name in the selected output.");
            local.Add(item.Name, item);
            if (Paths.Equals(safe, subject)) root = item;
        }
        if (root is null)
            throw new InvalidDataException("The selected subject DLL has no managed assembly metadata.");
        var seen = new HashSet<string>(Names);
        var queue = new Queue<string>();
        queue.Enqueue(root.Name);
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!seen.Add(name)) continue;
            foreach (var referenced in local[name].References.OrderBy(x => x, StringComparer.Ordinal))
                if (local.ContainsKey(referenced) && !seen.Contains(referenced)) queue.Enqueue(referenced);
        }
        // Only assemblies reachable from the subject are passed to the generated compiler.
        // Local system/framework dependencies absent here remain resolved by the SDK.
        return seen.Select(name => local[name])
            .OrderBy(a => a.Name, StringComparer.Ordinal)
            .Select(a => (a.Name, a.Path)).ToArray();
    }

    internal static int ReplaceProjectReferences(XDocument project, WorkspaceGuard guard, string subjectDll)
    {
        if (project.Root is null) throw new InvalidDataException("Generated project has no root.");
        var references = Resolve(guard, subjectDll);
        foreach (var entry in project.Descendants().Where(x => x.Name.LocalName == "ProjectReference").ToArray())
            entry.Remove();
        project.Root.Add(new XElement("ItemGroup", references.Select(reference =>
            new XElement("Reference", new XAttribute("Include", reference.Name),
                new XElement("HintPath", reference.Path), new XElement("Private", "true")))));
        return references.Count;
    }

    private static LocalAssembly? ReadMetadata(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return null;
            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly) return null;
            var name = metadata.GetString(metadata.GetAssemblyDefinition().Name);
            if (name.Length is < 1 or > 200)
                throw new InvalidDataException("Invalid local assembly identity in experiment dependency closure.");
            var references = metadata.AssemblyReferences
                .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
                .Where(x => x.Length > 0)
                .Distinct(Names).ToArray();
            return new LocalAssembly(name, path, references);
        }
        catch (BadImageFormatException) { return null; } // Native DLL, not a managed compile reference.
    }
}
