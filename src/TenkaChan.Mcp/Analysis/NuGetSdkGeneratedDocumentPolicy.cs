using System.Text.RegularExpressions;

namespace TenkaChan.Mcp.Analysis;

/// <summary>
/// Narrow, reviewed exclusion for known NuGet-package/SDK compile items that
/// MSBuildWorkspace can expose as source documents outside TENKA_ALLOWED_ROOTS.
/// This is a source-index exclusion, NEVER permission to read arbitrary NuGet files.
/// The catalog is exact (package id + layout + file): new package-injected patterns
/// require review and a dedicated regression test, and unknown packages fail closed.
/// </summary>
internal sealed class NuGetSdkGeneratedDocumentPolicy
{
    private readonly string _packagesRoot;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly Regex VersionPattern = new(@"^\d+(?:\.\d+){1,3}(?:-[A-Za-z0-9.-]+)?$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static readonly Regex TargetFrameworkFolderPattern = new(@"^net(?:\d+(?:\.\d+)?|standard\d+\.\d+)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

    /// <summary>A single reviewed package-injected compile source, laid out as
    /// &lt;package&gt;/&lt;version&gt;/build[/&lt;tfm&gt;]/&lt;file&gt;.</summary>
    private sealed record ReviewedSource(string PackageId, bool HasTargetFrameworkFolder, string FileName);

    private static readonly ReviewedSource[] Reviewed =
    [
        // Microsoft.NET.Test.Sdk injects a synthetic entry point into test projects.
        new("microsoft.net.test.sdk", HasTargetFrameworkFolder: true, "Microsoft.NET.Test.Sdk.Program.cs"),
        // TunnelVisionLabs.ReferenceAssemblyAnnotator injects nullable attributes for older targets.
        new("tunnelvisionlabs.referenceassemblyannotator", HasTargetFrameworkFolder: false, "NullableAttributes.cs"),
    ];

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

        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4) return false;
        if (!VersionPattern.IsMatch(parts[1])) return false;
        if (!parts[2].Equals("build", StringComparison.OrdinalIgnoreCase)) return false;
        var fileName = parts[^1];
        var middle = parts[3..^1]; // TFM folder between "build" and the file, when present.

        // Exact reviewed package + layout + file only. Never ignore a caller's linked
        // source simply because it lives somewhere under ~/.nuget/packages.
        var matched = Reviewed.Any(entry =>
            parts[0].Equals(entry.PackageId, StringComparison.OrdinalIgnoreCase) &&
            fileName.Equals(entry.FileName, StringComparison.OrdinalIgnoreCase) &&
            (entry.HasTargetFrameworkFolder
                ? middle.Length == 1 && TargetFrameworkFolderPattern.IsMatch(middle[0])
                : middle.Length == 0));
        if (!matched) return false;

        // Do not silently exempt a symlink/junction masquerading as a package helper.
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
