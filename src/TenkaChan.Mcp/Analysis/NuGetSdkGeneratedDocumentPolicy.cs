using System.Text.RegularExpressions;

namespace TenkaChan.Mcp.Analysis;

/// <summary>
/// Very narrow exclusion for a known SDK-supplied synthetic compile item that
/// MSBuildWorkspace can expose as a source document outside TENKA_ALLOWED_ROOTS.
/// This is a source-index exclusion, NEVER permission to read arbitrary NuGet files.
/// New package-generated patterns require review and a dedicated regression test.
/// </summary>
internal sealed class NuGetSdkGeneratedDocumentPolicy
{
    private readonly string _packagesRoot;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal NuGetSdkGeneratedDocumentPolicy(string? packagesRoot = null)
    {
        _packagesRoot = Path.GetFullPath(packagesRoot
            ?? Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget", "packages"));
    }

    internal bool CanExclude(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !Path.IsPathFullyQualified(filePath))
            return false;
        string full;
        try { full = Path.GetFullPath(filePath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }

        var relative = Path.GetRelativePath(_packagesRoot, full);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison) ||
            relative.StartsWith(".." + Path.AltDirectorySeparatorChar, PathComparison))
            return false;

        // Exact NuGet package + generated file only. Never ignore a caller's linked
        // source simply because it lives somewhere under ~/.nuget/packages.
        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 ||
            !parts[0].Equals("microsoft.net.test.sdk", StringComparison.OrdinalIgnoreCase) ||
            !parts[2].Equals("build", StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(parts[1], @"^\d+(?:\.\d+){1,3}(?:-[A-Za-z0-9.-]+)?$",
                RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) ||
            !Regex.IsMatch(parts[3], @"^net(?:\d+(?:\.\d+)?|standard\d+\.\d+)$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100)) ||
            !parts[4].Equals("Microsoft.NET.Test.Sdk.Program.cs", StringComparison.OrdinalIgnoreCase))
            return false;

        // Do not silently exempt a symlink/junction masquerading as an SDK helper.
        if (!File.Exists(full)) return false;
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if (!File.Exists(current) && !Directory.Exists(current)) return false;
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        return true;
    }
}
