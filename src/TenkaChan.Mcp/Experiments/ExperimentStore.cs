using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Experiments;

public sealed record ExperimentManifest(
    string Id, string Kind, string RepositoryRoot, string ProjectRelativePath,
    string CreatedUtc, string SourceSha256);

public sealed record ExperimentReceipt(string Id, string Kind, string Directory,
    string SourceDiagnostics, string Message);

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
        string kind, string generatedCode)
    {
        if (kind is not ("test" or "benchmark"))
            throw new ArgumentException("kind must be test or benchmark.");
        if (string.IsNullOrWhiteSpace(generatedCode) || generatedCode.Length > 60000)
            throw new ArgumentException("generatedCode must contain 1 to 60000 characters.");
        var root = _guard.RequireDirectory(repositoryRoot);
        var project = _guard.RequireProjectWithin(root, projectRelativePath);
        var relative = Path.GetRelativePath(root, project).Replace('\\', '/');

        var syntax = CSharpSyntaxTree.ParseText(generatedCode);
        var syntaxErrors = syntax.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error)
            .Take(10).Select(d => d.ToString()).ToArray();
        if (syntaxErrors.Length > 0)
            throw new ArgumentException("Generated C# syntax errors: " + string.Join("; ", syntaxErrors));

        // All user-provided code remains data; it is never executed on the MCP host.
        var id = Guid.NewGuid().ToString("N");
        var directory = Path.Combine(_storeRoot, "experiments", id);
        Directory.CreateDirectory(directory);
        var projectName = kind == "test" ? "GeneratedTests.csproj" : "GeneratedBenchmarks.csproj";
        var references = new XElement("ItemGroup",
            new XElement("ProjectReference", new XAttribute("Include", "../repo/" + relative)));
        var packages = kind == "test"
            ? new XElement("ItemGroup",
                new XElement("PackageReference", new XAttribute("Include", "Microsoft.NET.Test.Sdk"), new XAttribute("Version", "17.14.1")),
                new XElement("PackageReference", new XAttribute("Include", "xunit"), new XAttribute("Version", "2.9.3")),
                new XElement("PackageReference", new XAttribute("Include", "xunit.runner.visualstudio"), new XAttribute("Version", "3.1.5")))
            : new XElement("ItemGroup",
                new XElement("PackageReference", new XAttribute("Include", "BenchmarkDotNet"), new XAttribute("Version", "0.15.8")));
        var proj = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup",
                new XElement("TargetFramework", "net10.0"),
                new XElement("ImplicitUsings", "enable"),
                new XElement("Nullable", "enable"),
                new XElement("IsTestProject", kind == "test" ? "true" : "false"),
                new XElement("OutputType", kind == "test" ? "Library" : "Exe")),
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
        var manifest = new ExperimentManifest(id, kind, root, relative, DateTimeOffset.UtcNow.ToString("O"), digest);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, _json));
        return new ExperimentReceipt(id, kind, directory, "syntax_valid",
            "Prepared only; not executed. The resulting project requires an isolated Docker runner and prewarmed offline NuGet cache.");
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
        return (manifest, directory);
    }
}
