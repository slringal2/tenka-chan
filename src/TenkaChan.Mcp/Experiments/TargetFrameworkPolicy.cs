using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace TenkaChan.Mcp.Experiments;

/// <summary>
/// The analyzer host intentionally remains net10.0. This policy selects a
/// separate target runtime for generated experiments so net8.0 and later
/// applications are measured with THEIR runtime, rather than the host runtime.
/// </summary>
public sealed record TargetFrameworkSelection(string TargetFramework, int MajorVersion,
    string SdkImage, string Resolution)
{
    public static TargetFrameworkSelection FromValidated(string framework, string resolution)
    {
        var match = Regex.Match(framework, @"^net(?<version>\d{1,2})\.0$",
            RegexOptions.CultureInvariant);
        if (!match.Success || !int.TryParse(match.Groups["version"].Value,
                NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
            version < 8 || version > 20)
            throw new ArgumentException(
                "Experiments require a portable net8.0 or later .NET TFM (up to net20.0). " +
                "Framework-specific Windows/Android/iOS TFMs are not supported by the Linux worker.");
        return new TargetFrameworkSelection(framework, version,
            "mcr.microsoft.com/dotnet/sdk:" + version + ".0", resolution);
    }
}

public static class TargetFrameworkPolicy
{
    /// <summary>
    /// Read literal target frameworks only. Never evaluate MSBuild properties
    /// on the MCP host for this purpose. When TFM comes from an import or a
    /// property expression the caller must explicitly declare it, then the
    /// isolated restore/build acts as the final compatibility check.
    /// </summary>
    public static TargetFrameworkSelection Resolve(string projectPath,
        string? requestedFramework = null)
    {
        using var reader = XmlReader.Create(projectPath, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = 1_000_000
        });
        var doc = XDocument.Load(reader);
        var literal = doc.Descendants()
            .Where(n => n.Name.LocalName is "TargetFramework" or "TargetFrameworks")
            .SelectMany(n => (n.Value ?? "").Split(';', StringSplitOptions.TrimEntries |
                StringSplitOptions.RemoveEmptyEntries))
            .Where(value => !value.Contains("$(", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(requestedFramework))
        {
            var requested = TargetFrameworkSelection.FromValidated(
                requestedFramework.Trim(), "explicit");
            if (literal.Length > 0 && !literal.Contains(requested.TargetFramework,
                    StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException(
                    $"Requested target {requested.TargetFramework} is not declared by the project. " +
                    "Select a declared TargetFramework.");
            return requested;
        }

        var supported = literal.Select(value =>
        {
            try { return TargetFrameworkSelection.FromValidated(value, "project_file"); }
            catch (ArgumentException) { return null; }
        }).Where(selection => selection is not null)
          .Cast<TargetFrameworkSelection>()
          .OrderBy(selection => selection.MajorVersion).ToArray();
        if (supported.Length > 0)
        {
            // Multi-target projects default to their lowest supported TFM.
            // The user can opt into another declared TFM explicitly.
            return supported[0];
        }

        if (literal.Length > 0)
            throw new ArgumentException("Project has no portable net8.0-or-newer framework. " +
                "Actual TFMs: " + string.Join(", ", literal.Take(10)));
        throw new ArgumentException(
            "Target Framework not found as a literal .csproj property. " +
            "Pass targetFramework explicitly when it is supplied by Directory.Build.props or an MSBuild expression.");
    }

    /// <summary>
    /// Limit method-selection to a class-and-method filter. Unbounded "*" or
    /// arbitrary JIT dumps would consume excessive time/storage.
    /// </summary>
    public static string ValidateJitFilter(string selector)
    {
        if (string.IsNullOrWhiteSpace(selector) ||
            selector.Length > 160 ||
            !Regex.IsMatch(selector,
                @"^[A-Za-z_][A-Za-z0-9_+.<>-\x60\[\],*]*:[A-Za-z_][A-Za-z0-9_<>*\x60]*$",
                RegexOptions.CultureInvariant))
            throw new ArgumentException(
                "JIT filter must identify a class and method (e.g. Calculator:Sum), " +
                "without whitespace or shell control characters.");
        return selector;
    }
}
