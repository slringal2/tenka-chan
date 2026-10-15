using System.Xml;
using System.Xml.Linq;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class ExperimentFrameworkPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tenka-framework-" + Guid.NewGuid().ToString("N"));
    private string Project(string frameworks)
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "Library.csproj");
        File.WriteAllText(file, "<Project><PropertyGroup><TargetFrameworks>" + frameworks + "</TargetFrameworks></PropertyGroup></Project>");
        return file;
    }
    [Theory] [InlineData("net8.0")] [InlineData("net9.0")] [InlineData("net10.0")]
    public void PortableTargetsKeepTheirOwnRuntime(string tfm)
    {
        var selection = ExperimentFrameworkPolicy.Resolve(Project(tfm));
        Assert.Equal(tfm, selection.SubjectTargetFramework); Assert.Equal(tfm, selection.Runtime.TargetFramework);
    }
    [Theory] [InlineData("netstandard2.0")] [InlineData("netstandard2.1")]
    public void StandardDefaultsToDisclosedNet8BaselineNotAnalyzerHost(string tfm)
    {
        var file = Project(tfm); var before = File.ReadAllBytes(file);
        var selection = ExperimentFrameworkPolicy.Resolve(file);
        Assert.Equal(tfm, selection.SubjectTargetFramework); Assert.Equal("net8.0", selection.Runtime.TargetFramework);
        Assert.Contains("not_production", selection.Resolution); Assert.Equal(before, File.ReadAllBytes(file));
    }
    [Theory] [InlineData("net8.0")] [InlineData("net9.0")] [InlineData("net10.0")]
    public void StandardAllowsExplicitExperimentRuntime(string runtime)
    {
        var selection = ExperimentFrameworkPolicy.Resolve(Project("netstandard2.0"), runtimeFramework: runtime);
        Assert.Equal("netstandard2.0", selection.SubjectTargetFramework); Assert.Equal(runtime, selection.Runtime.TargetFramework);
        Assert.Equal("explicit_experiment_runtime", selection.Resolution);
    }
    [Fact] public void MixedFrameworksKeepLegacyPortablePreferenceUnlessSubjectIsExplicit()
    {
        var project = Project("netstandard2.0;net10.0;net8.0");
        Assert.Equal("net8.0", ExperimentFrameworkPolicy.Resolve(project).SubjectTargetFramework);
        var selection = ExperimentFrameworkPolicy.Resolve(project, "netstandard2.0", "net10.0");
        Assert.Equal("netstandard2.0", selection.SubjectTargetFramework); Assert.Equal("net10.0", selection.Runtime.TargetFramework);
    }
    [Fact] public void StandardOnlyMultiTargetChoiceIsDeterministic()
    {
        Assert.Equal("netstandard2.0", ExperimentFrameworkPolicy.Resolve(Project("netstandard2.1;netstandard2.0")).SubjectTargetFramework);
    }
    [Fact] public void ImportedFrameworkRequiresAnExplicitSubject()
    {
        var project = Project("$(LibraryTargetFramework)");
        Assert.Throws<ArgumentException>(() => ExperimentFrameworkPolicy.Resolve(project));
        Assert.Equal("netstandard2.0", ExperimentFrameworkPolicy.Resolve(project, "netstandard2.0").SubjectTargetFramework);
    }
    [Theory]
    [InlineData("netstandard2.1")] [InlineData("net7.0")] [InlineData("net8.0-windows")] [InlineData("net48")]
    public void RuntimeMustBePortableNet8OrNewer(string runtime) =>
        Assert.Throws<ArgumentException>(() => ExperimentFrameworkPolicy.Resolve(Project("netstandard2.0"), runtimeFramework: runtime));
    [Fact] public void DeclaredSubjectCannotBeReplacedWithUndeclaredStandardVersion() =>
        Assert.Throws<ArgumentException>(() => ExperimentFrameworkPolicy.Resolve(Project("netstandard2.0"), "netstandard2.1"));
    [Fact] public void PortableRuntimeCannotSilentlyChange() =>
        Assert.Throws<ArgumentException>(() => ExperimentFrameworkPolicy.Resolve(Project("net8.0"), runtimeFramework: "net10.0"));
    [Theory] [InlineData("netstandard1.6")] [InlineData("net48")]
    public void UnsupportedSubjectsAreNotRetargeted(string target) =>
        Assert.Throws<ArgumentException>(() => ExperimentFrameworkPolicy.Resolve(Project(target)));
    [Fact] public void XmlEntitiesAreRejectedWithoutReadingTheirTargets()
    {
        var path = Project("net8.0"); File.WriteAllText(path, "<!DOCTYPE Project [<!ENTITY e SYSTEM 'file:///never-read'>]><Project>&e;</Project>");
        Assert.Throws<XmlException>(() => ExperimentFrameworkPolicy.Resolve(path));
    }
    [Theory] [InlineData("netstandard2.0", "net8.0")] [InlineData("netstandard2.1", "net10.0")] [InlineData("net8.0", "net8.0")]
    public void StoredSupportedPairsAreValid(string subject, string runtime) => ExperimentFrameworkPolicy.ValidatePair(subject, runtime);
    [Theory] [InlineData("netstandard1.6", "net8.0")] [InlineData("net8.0", "net10.0")] [InlineData("netstandard2.0", "netstandard2.0")]
    public void StoredMismatchesAreRejected(string subject, string runtime) =>
        Assert.Throws<ArgumentException>(() => ExperimentFrameworkPolicy.ValidatePair(subject, runtime));

    [Theory] [InlineData("test", "GeneratedTests.csproj")] [InlineData("benchmark", "GeneratedBenchmarks.csproj")]
    public void PreparedManifestAndHarnessKeepSubjectAndRuntimeSeparate(string kind, string generatedProject)
    {
        var path = Project("netstandard2.0"); var original = File.ReadAllBytes(path);
        var store = new ExperimentStore(new WorkspaceGuard([_root], true, false));
        var receipt = store.Create(_root, "Library.csproj", kind, "public sealed class Probe { public int Value() => 7; }", runtimeFramework: "net10.0");
        try
        {
            var (manifest, dir) = store.Read(receipt.Id);
            Assert.Equal("netstandard2.0", manifest.SubjectTargetFramework);
            Assert.Equal("net10.0", manifest.TargetFramework); Assert.Equal("netstandard2.0", receipt.SubjectTargetFramework);
            Assert.Equal("net10.0", XDocument.Load(Path.Combine(dir, generatedProject)).Descendants("TargetFramework").Single().Value);
            Assert.Equal(original, File.ReadAllBytes(path)); Assert.False(File.Exists(Path.Combine(dir, "execution-report.json")));
        }
        finally { Directory.Delete(receipt.Directory, true); }
    }
    [Fact] public void UnsupportedStandardJitDoesNotPretendToCreateARunnableHarness()
    {
        Project("netstandard2.0"); var store = new ExperimentStore(new WorkspaceGuard([_root], true, false));
        Assert.Throws<ArgumentException>(() => store.Create(_root, "Library.csproj", "jit", "public class Probe {}", jitDisasmFilter: "Probe:Run"));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
