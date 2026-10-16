using System.Text;
using System.Xml;
using System.Xml.Linq;
using TenkaChan.Mcp.Agent;

namespace TenkaChan.Mcp.Experiments;

/// <summary>A frozen workload, not the result of its old standalone experiment.</summary>
public sealed record ProposalGeneratedFixture(string ExperimentId, string Kind, string SubjectProject,
    string SubjectTargetFramework, string RuntimeTargetFramework, string SourceSha256,
    string TemplateSha256, string RunnerSha256, IReadOnlyList<ProposalInput> Files);

/// <summary>
/// Reuses ExperimentStore's generated source/project/runner without running the
/// standalone executor or referencing its original DLL. Each ProjectReference
/// builds the subject in exactly one proposal copy. These are NOT sandboxes.
/// </summary>
public sealed class ProposalGeneratedExperiments(ChangeProposalStore proposals, ExperimentStore experiments)
{
    private const string Offline = "<configuration><packageSources><clear /></packageSources></configuration>";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] Variants = ["baseline", "candidate"];
    private static string ProjectName(string kind) => kind switch
    {
        "test" => "GeneratedTests.csproj", "benchmark" => "GeneratedBenchmarks.csproj",
        _ => throw new ArgumentException("Generated proposal fixtures must be test or benchmark experiments.")
    };
    internal static string Selection(ProposalGeneratedFixture f) => "generated:" + f.Kind + ":" + f.ExperimentId;
    private static string RelativeFolder(string kind, string variant) => "generated/" + kind + "/" + variant;

    // This reads and freezes prepared source. Old execution reports/artifacts are
    // deliberately ignored: the subject source is different in each proposal copy.
    public ProposalGeneratedFixture Prepare(ChangeProposal p, string experimentId, string kind)
    {
        InvestigationFiles.Id(experimentId);
        var (manifest, directory) = experiments.Read(experimentId);
        if (manifest.Kind != kind) throw new ArgumentException("generated_fixture_kind_mismatch: Select a " + kind + " experiment.");
        var subject = ChangeProposalStore.Within(p.Root, p.Project);
        var original = Path.GetFullPath(Path.Combine(manifest.RepositoryRoot, manifest.ProjectRelativePath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(subject, original, comparison))
            throw new ArgumentException("generated_fixture_project_mismatch: Prepare the experiment for this proposal's subject project; it will not be retargeted to a different project.");
        var framework = manifest.SubjectTargetFramework ?? manifest.TargetFramework;
        ExperimentFrameworkPolicy.ValidatePair(framework, manifest.TargetFramework);
        foreach (var variant in Variants)
        {
            var copied = ChangeProposalStore.Within(proposals.CopyRoot(p.Id, variant), p.Project);
            var resolved = ExperimentFrameworkPolicy.Resolve(copied, framework, manifest.TargetFramework);
            if (resolved.SubjectTargetFramework != framework || resolved.Runtime.TargetFramework != manifest.TargetFramework)
                throw new InvalidOperationException("generated_fixture_framework_mismatch: The copied project does not match the prepared experiment framework.");
        }
        var source = ReadHashed(directory, "Generated.cs", manifest.SourceSha256, 500_000);
        var projectName = ProjectName(kind);
        var template = ReadHashed(directory, projectName, manifest.ProjectSha256, 64_000);
        var runner = kind == "benchmark" ? ReadHashed(directory, "Program.cs", manifest.RunnerSha256, 16_000) : null;
        var folder = proposals.Folder(p.Id);
        // Match the SDK policy nearest the copied subject, including nested global.json.
        var sdk = ReadSdkPolicy(p, folder);
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var variant in Variants)
        {
            var relative = RelativeFolder(kind, variant);
            var destination = ChangeProposalStore.Within(folder, relative);
            var copiedSubject = ChangeProposalStore.Within(proposals.CopyRoot(p.Id, variant), p.Project);
            var reference = Path.GetRelativePath(destination, copiedSubject).Replace('\\', '/');
            if (reference.IndexOfAny(new[] { ';', '*', '?', '%' }) >= 0 || reference.Contains("$(", StringComparison.Ordinal)
                || reference.Contains("@(", StringComparison.Ordinal))
                throw new InvalidOperationException("generated_fixture_path_unsupported: MSBuild-special characters in the copied project path require explicit escaping support.");
            payloads.Add(relative + "/" + projectName, RewriteProject(template, manifest.TargetFramework, framework, kind, reference));
            payloads.Add(relative + "/Generated.cs", source);
            if (runner is not null) payloads.Add(relative + "/Program.cs", runner);
            // Local boundaries isolate generated project settings from the subject's
            // settings. ProjectReference still uses the copied subject's own imports.
            foreach (var file in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
                payloads.Add(relative + "/" + file, Utf8.GetBytes("<Project />"));
            payloads.Add(relative + "/NuGet.Config", Utf8.GetBytes(Offline));
            payloads.Add(relative + "/global.json", sdk);
        }
        // Never overwrite a different prior workload. Identical incomplete staging
        // may be completed only before a validation plan has been published.
        foreach (var item in payloads)
        {
            var path = ChangeProposalStore.Within(folder, item.Key);
            if (File.Exists(path) && !InvestigationFiles.Bytes(path, 500_000).AsSpan().SequenceEqual(item.Value))
                throw new InvalidOperationException("generated_fixture_changed: Prior staging has different content. Prepare a new proposal; do not overwrite its workload.");
        }
        foreach (var item in payloads)
        {
            var path = ChangeProposalStore.Within(folder, item.Key);
            InvestigationFiles.DirectoryAt(Path.GetDirectoryName(path)!);
            if (File.Exists(path)) continue;
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(item.Value); stream.Flush(true);
        }
        var fixture = new ProposalGeneratedFixture(experimentId, kind, p.Project, framework, manifest.TargetFramework,
            manifest.SourceSha256, manifest.ProjectSha256, manifest.RunnerSha256,
            payloads.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => new ProposalInput(x.Key, InvestigationFiles.Hash(x.Value), x.Value.Length)).ToArray());
        Verify(proposals, p, fixture, kind, Selection(fixture));
        return fixture;
    }

    private byte[] ReadSdkPolicy(ChangeProposal p, string folder)
    {
        var root = proposals.CopyRoot(p.Id, "baseline");
        for (var directory = Path.GetDirectoryName(ChangeProposalStore.Within(root, p.Project));
             directory is not null && InvestigationFiles.Within(root, directory); directory = Path.GetDirectoryName(directory))
        {
            var file = Path.Combine(directory, "global.json");
            if (File.Exists(file)) return InvestigationFiles.Bytes(file, 16_000);
        }
        var inherited = Path.Combine(folder, "global.json");
        return File.Exists(inherited) ? InvestigationFiles.Bytes(inherited, 16_000) : Utf8.GetBytes("{}");
    }

    private static byte[] ReadHashed(string folder, string name, string expected, int limit)
    {
        if (expected is null || expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            throw new InvalidDataException("generated_fixture_hash_missing: Regenerate this legacy experiment so all fixture files have hashes.");
        var bytes = InvestigationFiles.Bytes(Path.Combine(folder, name), limit);
        if (InvestigationFiles.Hash(bytes) != expected)
            throw new InvalidDataException("generated_fixture_changed: Experiment source/project/runner changed after preparation.");
        return bytes;
    }
    private static byte[] RewriteProject(byte[] template, string runtime, string subject, string kind, string reference)
    {
        using var reader = XmlReader.Create(new MemoryStream(template), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64_000 });
        var xml = XDocument.Load(reader);
        var root = xml.Root ?? throw new InvalidDataException("Missing generated project root.");
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        { "Project", "PropertyGroup", "ItemGroup", "TargetFramework", "ImplicitUsings", "Nullable", "IsTestProject", "OutputType", "DebugType", "Optimize", "ProjectReference", "PackageReference" };
        if (root.Name != "Project" || (string?)root.Attribute("Sdk") != "Microsoft.NET.Sdk"
            || xml.Descendants().Any(e => e.Name.NamespaceName.Length != 0 || !allowed.Contains(e.Name.LocalName)))
            throw new InvalidDataException("generated_fixture_template_unsupported: Recreate the fixture using tenka_prepare_experiment.");
        var properties = root.Elements("PropertyGroup").SingleOrDefault()
            ?? throw new InvalidDataException("Missing generated project properties.");
        if (properties.Element("TargetFramework")?.Value != runtime
            || properties.Element("IsTestProject")?.Value != (kind == "test" ? "true" : "false")
            || properties.Element("OutputType")?.Value != (kind == "test" ? "Library" : "Exe"))
            throw new InvalidDataException("Generated project framework/kind differs from its manifest.");
        var references = xml.Descendants("ProjectReference").ToArray();
        if (references.Length != 1 || references[0].HasElements)
            throw new InvalidDataException("Generated fixture must have one simple subject ProjectReference.");
        var projectReference = references[0];
        projectReference.RemoveAttributes(); projectReference.SetAttributeValue("Include", reference);
        // Explicit framework selection must not become the analyzer's net10.0.
        projectReference.Add(new XElement("SetTargetFramework", "TargetFramework=" + subject),
            new XElement("SkipGetTargetFrameworkProperties", "true"));
        properties.Add(new XElement("EnableDefaultCompileItems", "false"));
        var compile = new XElement("ItemGroup", new XElement("Compile", new XAttribute("Include", "Generated.cs")));
        if (kind == "benchmark") compile.Add(new XElement("Compile", new XAttribute("Include", "Program.cs")));
        root.Add(compile);
        return Utf8.GetBytes(xml.ToString());
    }

    internal static string ProjectPath(ChangeProposalStore proposals, ChangeProposal p, ProposalGeneratedFixture fixture, string variant)
    {
        if (!Variants.Contains(variant, StringComparer.Ordinal)) throw new ArgumentException("Invalid proposal variant.");
        return ChangeProposalStore.Within(proposals.Folder(p.Id), RelativeFolder(fixture.Kind, variant) + "/" + ProjectName(fixture.Kind));
    }
    internal static void VerifyAll(ChangeProposalStore proposals, ChangeProposal p, ProposalValidation report)
    {
        Verify(proposals, p, report.GeneratedTest, "test", report.TestProject);
        Verify(proposals, p, report.GeneratedBenchmark, "benchmark", report.BenchmarkProject);
    }
    private static void Verify(ChangeProposalStore proposals, ChangeProposal p, ProposalGeneratedFixture? fixture, string kind, string selection)
    {
        if (fixture is null)
        {
            if (selection.StartsWith("generated:", StringComparison.Ordinal))
                throw new InvalidDataException("Generated validation selection has no frozen workload descriptor.");
            return;
        }
        InvestigationFiles.Id(fixture.ExperimentId);
        if (fixture.Kind != kind || fixture.SubjectProject != p.Project || selection != Selection(fixture))
            throw new InvalidDataException("Generated fixture selection does not match its proposal.");
        ExperimentFrameworkPolicy.ValidatePair(fixture.SubjectTargetFramework, fixture.RuntimeTargetFramework);
        var expectedNames = new List<string>();
        foreach (var variant in Variants)
        {
            var relative = RelativeFolder(kind, variant);
            foreach (var name in new[] { ProjectName(kind), "Generated.cs", "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props", "NuGet.Config", "global.json" })
                expectedNames.Add(relative + "/" + name);
            if (kind == "benchmark") expectedNames.Add(relative + "/Program.cs");
        }
        if (fixture.Files is null || fixture.Files.Count != expectedNames.Count
            || !fixture.Files.Select(f => f.Path).Order(StringComparer.Ordinal).SequenceEqual(expectedNames.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Generated fixture file inventory is incomplete or unexpected.");
        foreach (var file in fixture.Files)
        {
            var bytes = InvestigationFiles.Bytes(ChangeProposalStore.Within(proposals.Folder(p.Id), file.Path), 500_000);
            if (bytes.Length != file.Bytes || InvestigationFiles.Hash(bytes) != file.Hash)
                throw new InvalidDataException("generated_fixture_changed: Frozen source, project or runner changed; results must not be reused.");
            if (file.Path.EndsWith("/Generated.cs", StringComparison.Ordinal) && file.Hash != fixture.SourceSha256
                || file.Path.EndsWith("/Program.cs", StringComparison.Ordinal) && file.Hash != fixture.RunnerSha256)
                throw new InvalidDataException("Frozen workload does not match its prepared experiment hash.");
        }
        foreach (var variant in Variants)
        {
            var directory = Path.GetDirectoryName(ProjectPath(proposals, p, fixture, variant))!;
            var allowed = expectedNames.Where(n => n.StartsWith(RelativeFolder(kind, variant) + "/", StringComparison.Ordinal)).Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(path);
                if (name is "bin" or "obj") continue; // Outputs only; never compiled by our explicit Compile items.
                InvestigationFiles.NoLinks(path);
                if (!allowed.Contains(name) || Directory.Exists(path))
                    throw new InvalidDataException("generated_fixture_changed: Unexpected file in the frozen generated workload.");
            }
        }
    }
}
