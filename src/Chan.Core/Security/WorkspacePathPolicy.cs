namespace Chan.Core.Security;

/// <summary>
/// Language-independent read allowlist. Rejects reparse points in existing path
/// segments; it is not an OS sandbox or a race-free file-opening primitive.
/// Callers must still authorize project evaluation and process execution separately.
/// </summary>
public sealed class WorkspacePathPolicy
{
    private readonly string[] _roots;
    private readonly string _allowedRootsSetting;
    private readonly StringComparison _comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public WorkspacePathPolicy(IEnumerable<string> allowedRoots,
        string allowedRootsSetting = "the configured workspace allowlist")
    {
        _roots = allowedRoots.Select(Path.GetFullPath).Distinct(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        _allowedRootsSetting = allowedRootsSetting;
    }

    public string RequireDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        CheckAllowed(fullPath);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException(fullPath);
        CheckNoReparsePoints(fullPath);
        return fullPath;
    }

    public string RequireFile(string path, string extension)
    {
        var fullPath = Path.GetFullPath(path);
        CheckAllowed(fullPath);
        if (!string.Equals(Path.GetExtension(fullPath), extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Expected a {extension} file.");
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Project file not found", fullPath);
        CheckNoReparsePoints(fullPath);
        return fullPath;
    }

    private void CheckAllowed(string candidate)
    {
        if (_roots.Length == 0 || !_roots.Any(root => IsWithin(root, candidate)))
            throw new UnauthorizedAccessException(
                $"Path is not in {_allowedRootsSetting}. Configure an explicit directory allowlist.");
    }

    /// <summary>Lexical containment only; RequireFile/RequireDirectory also enforce the allowlist and reparse checks.</summary>
    public bool IsWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "." || (!Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, _comparison)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, _comparison));
    }

    private static void CheckNoReparsePoints(string path)
    {
        // Fail closed for links/reparse points in any existing path segment.
        var current = new FileInfo(path).FullName;
        while (current is not null)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Symlink/reparse paths are not permitted.");
            }
            var parent = Path.GetDirectoryName(current);
            if (parent is null || parent == current) break;
            current = parent;
        }
    }
}
