using Chan.Core.Security;

namespace TenkaChan.Mcp.Security;

/// <summary>
/// Tenka-specific project and execution permissions. Shared path validation is
/// delegated to Chan.Core without changing TENKA_* settings or C# project rules.
/// An allowlist is not a replacement for OS-level process sandboxing.
/// </summary>
public sealed class WorkspaceGuard
{
    private readonly WorkspacePathPolicy _paths;

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
        _paths = new WorkspacePathPolicy(allowedRoots, "TENKA_ALLOWED_ROOTS");
        TrustMsBuild = trustMsBuild;
        AllowDocker = allowDocker;
    }

    public bool TrustMsBuild { get; }
    public bool AllowDocker { get; }

    public string RequireDirectory(string path) => _paths.RequireDirectory(path);

    public string RequireFile(string path, string extension) => _paths.RequireFile(path, extension);

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
        if (!_paths.IsWithin(actualRoot, fullPath))
            throw new UnauthorizedAccessException("Project path escapes the chosen repository root.");
        return fullPath;
    }
}
