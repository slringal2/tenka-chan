namespace TenkaChan.Mcp.Security;

public sealed record BundledSampleInfo(bool Available, string Message,
    string? SampleDirectory = null, string? ProjectPath = null,
    string? TestProjectPath = null, string? BenchmarkProjectPath = null,
    string? AssemblyPath = null, bool AssemblyExists = false,
    string? TraceOutputDirectory = null);

/// <summary>Locate only the fixed bundled sample. No MSBuild, target execution or allowlist changes.</summary>
public sealed class BundledSampleCatalog(WorkspaceGuard guard)
{
    public BundledSampleInfo FindGameLab(string startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory) || !Path.IsPathFullyQualified(startDirectory))
            return new(false, "Sample discovery requires the server's absolute content root.");
        try
        {
            var current = new DirectoryInfo(startDirectory);
            for (var depth = 0; current is not null && depth < 8; depth++, current = current.Parent)
            {
                // Do not search user-supplied arbitrary paths or scan the drive recursively.
                if (!File.Exists(Path.Combine(current.FullName, "TenkaChan.slnx")) ||
                    !File.Exists(Path.Combine(current.FullName, "src", "TenkaChan.Web", "TenkaChan.Web.csproj"))) continue;
                var sample = Path.Combine(current.FullName, "samples", "Tenka.Sample.GameLab");
                if (!Directory.Exists(sample)) return new(false, "samples/Tenka.Sample.GameLab is missing. Update the source checkout.");
                sample = guard.RequireDirectory(sample);
                var project = guard.RequireFile(Path.Combine(sample, "Tenka.Sample.GameLab.csproj"), ".csproj");
                var tests = guard.RequireFile(Path.Combine(sample, "Tests", "Tenka.Sample.GameLab.Tests.csproj"), ".csproj");
                var benchmarks = guard.RequireFile(Path.Combine(sample, "Benchmarks", "Tenka.Sample.GameLab.Benchmarks.csproj"), ".csproj");
                var traces = guard.RequireDirectory(Path.Combine(sample, "profiles"));
                var assembly = Path.Combine(sample, "bin", "Release", "net8.0", "Tenka.Sample.GameLab.dll");
                var exists = File.Exists(assembly);
                if (exists) assembly = guard.RequireFile(assembly, ".dll");
                return new(true, "Select the sample, then explicitly load/build it. No action has been executed.",
                    sample, project, tests, benchmarks, assembly, exists, traces);
            }
            return new(false, "The sample was not found beside this server. Use a source checkout including samples.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(false, "The bundled sample is missing or outside TENKA_ALLOWED_ROOTS. Run setup.bat and select the bundled sample.");
        }
    }
}
