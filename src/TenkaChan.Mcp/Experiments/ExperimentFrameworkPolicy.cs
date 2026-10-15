using System.Xml;
using System.Xml.Linq;

namespace TenkaChan.Mcp.Experiments;

public sealed record ExperimentFrameworkSelection(string SubjectTargetFramework,
    TargetFrameworkSelection Runtime, string Resolution);

/// <summary>
/// Separates the library's compiled API target from the runtime of a detached experiment.
/// Does not evaluate MSBuild, install a runtime, or claim the selected runtime matches production.
/// Legacy portable .NET experiments keep their original same-framework selection policy.
/// </summary>
public static class ExperimentFrameworkPolicy
{
    public static bool IsStandardLibrary(string? framework) =>
        framework is "netstandard2.0" or "netstandard2.1";

    public static ExperimentFrameworkSelection Resolve(string projectPath,
        string? targetFramework = null, string? runtimeFramework = null)
    {
        using var reader = XmlReader.Create(projectPath, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = 1_000_000
        });
        var doc = XDocument.Load(reader);
        var declared = doc.Descendants()
            .Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks")
            .SelectMany(e => e.Value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Where(t => !t.Contains("$(", StringComparison.Ordinal))
            .Select(t => t.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        var requested = string.IsNullOrWhiteSpace(targetFramework) ? null : targetFramework.Trim().ToLowerInvariant();
        string? subject = requested;
        if (subject is null)
        {
            // Preserve existing behavior for mixed netstandard/net8+ multi-target libraries.
            var hasPortableTarget = declared.Any(IsPortableRuntime);
            if (!hasPortableTarget)
                subject = declared.Where(IsStandardLibrary).Order(StringComparer.Ordinal).FirstOrDefault();
        }
        if (!IsStandardLibrary(subject))
        {
            var legacy = TargetFrameworkPolicy.Resolve(projectPath, targetFramework);
            if (!string.IsNullOrWhiteSpace(runtimeFramework) &&
                !string.Equals(legacy.TargetFramework, runtimeFramework.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("For a portable .NET target, runtimeFramework must match targetFramework. " +
                    "Choose another declared target explicitly instead of silently measuring on another runtime.");
            return new(legacy.TargetFramework, legacy, legacy.Resolution);
        }
        if (declared.Length > 0 && !declared.Contains(subject!, StringComparer.Ordinal))
            throw new ArgumentException("The requested library framework is not declared by this project: " + subject);
        // netstandard is an API contract, never a runtime. net8.0 is a disclosed baseline,
        // not the analyzer's net10.0 host and not an inferred production environment.
        var explicitRuntime = !string.IsNullOrWhiteSpace(runtimeFramework);
        var runtime = TargetFrameworkSelection.FromValidated(explicitRuntime
            ? runtimeFramework!.Trim().ToLowerInvariant() : "net8.0",
            explicitRuntime ? "explicit_experiment_runtime" : "default_net8_baseline_not_production");
        return new(subject!, runtime, runtime.Resolution);
    }

    public static void ValidatePair(string subjectFramework, string runtimeFramework)
    {
        _ = TargetFrameworkSelection.FromValidated(runtimeFramework, "stored_experiment_runtime");
        if (IsStandardLibrary(subjectFramework)) return;
        _ = TargetFrameworkSelection.FromValidated(subjectFramework, "stored_subject_framework");
        if (subjectFramework != runtimeFramework)
            throw new ArgumentException("Portable .NET subject and experiment runtime must match.");
    }

    private static bool IsPortableRuntime(string value)
    {
        try { _ = TargetFrameworkSelection.FromValidated(value, "literal"); return true; }
        catch (ArgumentException) { return false; }
    }
}
