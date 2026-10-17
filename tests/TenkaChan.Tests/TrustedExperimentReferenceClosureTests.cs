using System.Xml.Linq;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class TrustedExperimentReferenceClosureTests
{
    [Fact]
    public void GeneratedHostProjectReferencesSubjectAndItsBuiltDependenciesWithoutProjectReferences()
    {
        // The MCP assembly references Chan.Core. Both are copied to the test output
        // by the SDK, so this exercises a real multi-assembly PE metadata closure.
        var subject = typeof(TrustedHostExperimentExecutor).Assembly.Location;
        var root = Path.GetDirectoryName(subject)!;
        var guard = new WorkspaceGuard([root], false, false);
        var project = XDocument.Parse("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><ProjectReference Include="../source/Target.csproj" /></ItemGroup>
            </Project>
            """);

        var count = TrustedExperimentReferenceClosure.ReplaceProjectReferences(project, guard, subject);

        Assert.True(count >= 2);
        Assert.Empty(project.Descendants("ProjectReference"));
        var names = project.Descendants("Reference").Select(r => (string?)r.Attribute("Include")).ToArray();
        Assert.Contains("TenkaChan.Mcp", names);
        Assert.Contains("Chan.Core", names);
        foreach (var reference in project.Descendants("Reference"))
        {
            Assert.True(File.Exists(reference.Element("HintPath")!.Value));
            Assert.Equal("true", reference.Element("Private")!.Value);
        }
    }

    [Fact]
    public void UnmanagedOrMissingSubjectIsNeverTreatedAsAUsableAssembly()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenka-reference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var subject = Path.Combine(root, "Subject.dll");
            File.WriteAllText(subject, "not a .NET assembly");
            var guard = new WorkspaceGuard([root], false, false);
            Assert.Throws<InvalidDataException>(() => TrustedExperimentReferenceClosure.Resolve(guard, subject));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DuplicateManagedIdentityIsRejectedRatherThanRandomlyPicked()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenka-reference-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var assembly = typeof(TrustedHostExperimentExecutor).Assembly.Location;
            var subject = Path.Combine(root, "Subject.dll");
            File.Copy(assembly, subject);
            File.Copy(assembly, Path.Combine(root, "Duplicated.dll"));
            var guard = new WorkspaceGuard([root], false, false);
            var error = Assert.Throws<InvalidOperationException>(() =>
                TrustedExperimentReferenceClosure.Resolve(guard, subject));
            Assert.Contains("experiment_reference_ambiguous", error.Message);
        }
        finally { Directory.Delete(root, true); }
    }
}
