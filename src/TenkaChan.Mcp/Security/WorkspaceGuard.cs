namespace TenkaChan.Mcp.Security;

/// <summary>
/// Controls where the MCP service may read projects and whether it may evaluate
/// MSBuild projects or start the isolated Docker worker. This is an allowlist,
/// not a replacement for OS-level process sandboxing.
/// </summary>
public sealed class WorkspaceGuard
{
    private readonly string[] _roots;
    private readonly StringComparison _comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public WorkspaceGuard()
        : this(
            (Environment.GetEnvironmentVariable("TENKA_ALLOWED_ROOTS") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Environment.GetEnvironmentVariable("TENKA_TRUST_MSBUILD") == "1",
            Environment.GetEnvironmentVariable("TENKA_ENABLE_DOCKER") == "1")
    {
    }

    public WorkspaceGuard(IEnumerable<string> allowedRoots, bool trustMsBuild, bool allowDocker)
    {
        _roots = allowedRoots.Select(Path.GetFullPath).Distinct(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        TrustMsBuild = trustMsBuild;
        AllowDocker = allowDocker;
    }

    public bool TrustMsBuild { get; }
    public bool AllowDocker { get; }

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

    public string RequireSolutionOrProject(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".sln" or ".slnx" or ".csproj"))
            throw new ArgumentException("Provide a .sln, .slnx or .csproj path.");
        return RequireFile(path, ext);
    }

    public string RequireProjectWithin(string root, string relativeProject)
    {
        var actualRoot = RequireDirectory(root);
        if (Path.IsPathRooted(relativeProject))
            throw new ArgumentException("Project path must be relative.");
        var fullPath = RequireFile(Path.Combine(actualRoot, relativeProject), ".csproj");
        if (!IsInside(actualRoot, fullPath))
            throw new UnauthorizedAccessException("Project path escapes the chosen repository root.");
        return fullPath;
    }

    private void CheckAllowed(string candidate)
    {
        if (_roots.Length == 0 || !_roots.Any(root => IsInside(root, candidate)))
            throw new UnauthorizedAccessException(
                "Path is not in TENKA_ALLOWED_ROOTS. Configure an explicit directory allowlist.");
    }

    private bool IsInside(string root, string candidate)
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
