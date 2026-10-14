using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Experiments;

public sealed record ExperimentManifest(
    string Id, string Kind, string RepositoryRoot, string ProjectRelativePath,
    string CreatedUtc, string SourceSha256, string ProjectSha256 = "",
    string TargetFramework = "net10.0", string? JitFilter = null,
    string RunnerSha256 = "");

public sealed record ExperimentReceipt(string Id, string Kind, string Directory,
    string SourceDiagnostics, string Message)
{
    public string TargetFramework { get; init; } = "net10.0";
}

public sealed record ExperimentListItem(string Id, string Kind, string CreatedUtc,
    string RepositoryRoot, string ProjectRelativePath, string Status,
    int? TestsPassed, int? TestsFailed, int Benchmarks)
{
    public string TargetFramework { get; init; } = "net10.0";
}

public sealed class ExperimentStore
{
    private readonly WorkspaceGuard _guard;
    private readonly string _storeRoot;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public ExperimentStore(WorkspaceGuard guard)
    {
        _guard = guard;
        _storeRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("TENKA_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tenka-chan"));
    }

    public ExperimentReceipt Create(string repositoryRoot, string projectRelativePath,
        string kind, string generatedCode, string? targetFramework = null,
        string? jitDisasmFilter = null)
    {
        if (kind is not ("test" or "benchmark" or "jit"))
            throw new ArgumentException("kind must be test, benchmark or jit.");
        if (kind == "jit")
            jitDisasmFilter = TargetFrameworkPolicy.ValidateJitFilter(
                jitDisasmFilter ?? "");
        else if (jitDisasmFilter is not null)
            throw new ArgumentException("jitDisasmFilter is valid only for kind=jit.");
        if (string.IsNullOrWhiteSpace(generatedCode) || generatedCode.Length > 60000)
            throw new ArgumentException("generatedCode must contain 1 to 60000 characters.");
        var root = _guard.RequireDirectory(repositoryRoot);
        var project = _guard.RequireProjectWithin(root, projectRelativePath);
        var relative = Path.GetRelativePath(root, project).Replace('\\', '/');
        var selection = TargetFrameworkPolicy.Resolve(project, targetFramework);

        var syntax = CSharpSyntaxTree.ParseText(generatedCode);
        var syntaxErrors = syntax.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)
            .Take(10).Select(d => d.ToString()).ToArray();
        if (syntaxErrors.Length > 0)
            throw new ArgumentException("Generated C# syntax errors: " + string.Join("; ", syntaxErrors));

        // All user-provided code remains data; it is never executed on the MCP host.
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(_storeRoot, "experiments", id);
        Directory.CreateDirectory(directory);
        var projectName = kind switch
        {
            "test" => "GeneratedTests.csproj",
            "benchmark" => "GeneratedBenchmarks.csproj",
            _ => "GeneratedJit.csproj"
        };
        var references = new XElement("ItemGroup",
            new XElement("ProjectReference", new XAttribute("Include", "../repo/" + relative)));
        XElement? packages = kind switch
        {
            "test" => new XElement("ItemGroup",
                new XElement("PackageReference", new XAttribute("Include", "Microsoft.NET.Test.Sdk"), new XAttribute("Version", "17.14.1")),
                new XElement("PackageReference", new XAttribute("Include", "xunit"), new XAttribute("Version", "2.9.3")),
                new XElement("PackageReference", new XAttribute("Include", "xunit.runner.visualstudio"), new XAttribute("Version", "3.1.5"))),
            "benchmark" => new XElement("ItemGroup",
                new XElement("PackageReference", new XAttribute("Include", "BenchmarkDotNet"), new XAttribute("Version", "0.15.8"))),
            _ => null
        };
        var proj = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup",
                new XElement("TargetFramework", selection.TargetFramework),
                new XElement("ImplicitUsings", "enable"),
                new XElement("Nullable", "enable"),
                new XElement("IsTestProject", kind == "test" ? "true" : "false"),
                new XElement("OutputType", kind == "test" ? "Library" : "Exe"),
                new XElement("DebugType", "portable"),
                new XElement("Optimize", "true")),
            references, packages);
        File.WriteAllText(Path.Combine(directory, projectName), proj.ToString());
        File.WriteAllText(Path.Combine(directory, "Generated.cs"), generatedCode);
        if (kind == "benchmark")
        {
            File.WriteAllText(Path.Combine(directory, "Program.cs"),
                "using BenchmarkDotNet.Running;\nBenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);\n");
        }
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(generatedCode)));
        var projectSha = Sha256File(Path.Combine(directory, projectName));
        var runnerHash = kind == "benchmark"
            ? Sha256File(Path.Combine(directory, "Program.cs")) : "";
        var manifest = new ExperimentManifest(id, kind, root, relative,
            DateTimeOffset.UtcNow.ToString("O"), digest, projectSha,
            selection.TargetFramework, jitDisasmFilter, runnerHash);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, _json));
        return new ExperimentReceipt(id, kind, directory, "syntax_valid",
            $"Prepared {selection.TargetFramework} ({selection.Resolution}); not executed. " +
            "Run with Docker, or explicitly approve UNSANDBOXED host execution with an offline package cache.")
        {
            TargetFramework = selection.TargetFramework
        };
    }

    public (ExperimentManifest Manifest, string Directory) Read(string experimentId)
    {
        if (!Guid.TryParseExact(experimentId, "N", out _))
            throw new ArgumentException("Invalid experiment ID.");
        var directory = Path.Combine(_storeRoot, "experiments", experimentId);
        var file = Path.Combine(directory, "manifest.json");
        if (!File.Exists(file))
            throw new FileNotFoundException("Experiment not found", file);
        var manifest = JsonSerializer.Deserialize<ExperimentManifest>(File.ReadAllText(file))
            ?? throw new InvalidDataException("Manifest invalid.");
        if (manifest.Id != experimentId)
            throw new InvalidDataException("Manifest ID mismatch.");
        _guard.RequireProjectWithin(manifest.RepositoryRoot, manifest.ProjectRelativePath);
        var source = Path.Combine(directory, "Generated.cs");
        _ = TargetFrameworkSelection.FromValidated(manifest.TargetFramework, "stored");
        if (manifest.Kind == "jit")
            _ = TargetFrameworkPolicy.ValidateJitFilter(manifest.JitFilter ?? "");
        var projectName = manifest.Kind switch
        {
            "test" => "GeneratedTests.csproj",
            "benchmark" => "GeneratedBenchmarks.csproj",
            "jit" => "GeneratedJit.csproj",
            _ => throw new InvalidDataException("Invalid experiment kind.")
        };
        var project = Path.Combine(directory, projectName);
        if (!File.Exists(source) || !File.Exists(project)
            || !string.Equals(Sha256File(source), manifest.SourceSha256, StringComparison.Ordinal)
            || (manifest.ProjectSha256.Length > 0
                && !string.Equals(Sha256File(project), manifest.ProjectSha256, StringComparison.Ordinal))
            || (manifest.Kind == "benchmark" && manifest.RunnerSha256.Length > 0 &&
                (!File.Exists(Path.Combine(directory, "Program.cs")) ||
                 !string.Equals(Sha256File(Path.Combine(directory, "Program.cs")),
                     manifest.RunnerSha256, StringComparison.Ordinal))))
            throw new InvalidDataException("Generated experiment files changed after preparation.");
        return (manifest, directory);
    }

    internal string CreateArtifactsDirectory(string experimentId)
    {
        var (_, directory) = Read(experimentId);
        var artifacts = Path.Combine(directory, "artifacts");
        Directory.CreateDirectory(artifacts);
        // The sandbox runs as UID 65532. This grants write access only inside a
        // dedicated artifacts directory; repository and generated source remain RO.
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(artifacts,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute);
        }
        return artifacts;
    }

    internal void SaveExecutionResult(ExecutionResult report)
    {
        var (_, directory) = Read(report.ExperimentId);
        var target = Path.Combine(directory, "execution-report.json");
        var temporary = Path.Combine(directory, ".execution-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(report, _json));
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public IReadOnlyList<ExperimentListItem> ListRecent(int limit = 50)
    {
        var experimentsPath = Path.Combine(_storeRoot, "experiments");
        if (!Directory.Exists(experimentsPath)) return [];
        var results = new List<ExperimentListItem>();
        foreach (var dir in Directory.EnumerateDirectories(experimentsPath)
            .OrderByDescending(Directory.GetCreationTimeUtc)
            .Take(Math.Clamp(limit, 1, 100) * 5))
        {
            if (results.Count >= Math.Clamp(limit, 1, 100)) break;
            if ((new DirectoryInfo(dir).Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var id = Path.GetFileName(dir);
            if (!Guid.TryParseExact(id, "N", out _)) continue;
            try
            {
                var (manifest, directory) = Read(id);
                var reportPath = Path.Combine(directory, "execution-report.json");
                var execution = File.Exists(reportPath) ? ReadExecutionResult(id) : null;
                results.Add(new ExperimentListItem(manifest.Id, manifest.Kind, manifest.CreatedUtc,
                    manifest.RepositoryRoot, manifest.ProjectRelativePath,
                    execution?.Status ?? "prepared",
                    execution?.Tests?.Passed, execution?.Tests?.Failed,
                    execution?.Benchmarks.Count ?? 0)
                {
                    TargetFramework = manifest.TargetFramework
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or InvalidDataException or System.Text.Json.JsonException
                or ArgumentException)
            {
                // An old, invalid, or no-longer-allowlisted experiment cannot
                // appear as a valid artifact in the dashboard.
            }
        }
        return results.OrderByDescending(x => x.CreatedUtc, StringComparer.Ordinal).ToArray();
    }

    public ExecutionResult ReadExecutionResult(string experimentId)
    {
        var (_, directory) = Read(experimentId);
        var target = Path.Combine(directory, "execution-report.json");
        if (!File.Exists(target))
            throw new FileNotFoundException("Experiment has not produced a stored execution report.", target);
        if (new FileInfo(target).Length > 1_000_000)
            throw new InvalidDataException("Execution report exceeds maximum size.");
        return JsonSerializer.Deserialize<ExecutionResult>(File.ReadAllText(target))
            ?? throw new InvalidDataException("Execution report is invalid.");
    }

    private static string Sha256File(string filePath) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(filePath)));
}
