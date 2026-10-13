using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class TargetFrameworkPolicyTests
{
    [Theory]
    [InlineData("net8.0", 8, "mcr.microsoft.com/dotnet/sdk:8.0")]
    [InlineData("net9.0", 9, "mcr.microsoft.com/dotnet/sdk:9.0")]
    [InlineData("net10.0", 10, "mcr.microsoft.com/dotnet/sdk:10.0")]
    [InlineData("net11.0", 11, "mcr.microsoft.com/dotnet/sdk:11.0")]
    public void SupportsNet8AndNewerPortableFrameworks(string tfm, int major, string image)
    {
        var result = TargetFrameworkSelection.FromValidated(tfm, "test");
        Assert.Equal(major, result.MajorVersion);
        Assert.Equal(image, result.SdkImage);
    }

    [Theory]
    [InlineData("net7.0")]
    [InlineData("netstandard2.1")]
    [InlineData("net8.0-windows")]
    [InlineData("net8.0-android")]
    [InlineData("NET8.0")]
    [InlineData("../../app")]
    public void RejectsUnsupportedAndPlatformSpecificTfms(string tfm)
    {
        Assert.Throws<ArgumentException>(() =>
            TargetFrameworkSelection.FromValidated(tfm, "test"));
    }

    [Fact]
    public void MultiTargetDefaultsToLowestCompatibleTfmAndSupportsExplicitOverride()
    {
        WithProject("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFrameworks>net8.0;net9.0;net10.0</TargetFrameworks></PropertyGroup>
            </Project>
            """, path =>
            {
                Assert.Equal("net8.0", TargetFrameworkPolicy.Resolve(path).TargetFramework);
                Assert.Equal("net10.0", TargetFrameworkPolicy.Resolve(path, "net10.0").TargetFramework);
                Assert.Throws<ArgumentException>(() =>
                    TargetFrameworkPolicy.Resolve(path, "net11.0"));
            });
    }

    [Fact]
    public void RejectsOldProjectAndRequiresExplicitTfmForMsbuildProperties()
    {
        WithProject("""
            <Project><PropertyGroup><TargetFramework>net7.0</TargetFramework></PropertyGroup></Project>
            """, path =>
                Assert.Throws<ArgumentException>(() => TargetFrameworkPolicy.Resolve(path)));

        WithProject("""
            <Project><PropertyGroup><TargetFramework>$(FrameworkFromProps)</TargetFramework></PropertyGroup></Project>
            """, path =>
            {
                Assert.Throws<ArgumentException>(() => TargetFrameworkPolicy.Resolve(path));
                Assert.Equal("net8.0", TargetFrameworkPolicy.Resolve(path, "net8.0").TargetFramework);
            });
    }

    [Theory]
    [InlineData("Calculator:Sum")]
    [InlineData("Calculator:*")]
    [InlineData("My.Namespace.Calculator:Compute")]
    public void AcceptsNarrowJitMethods(string filter)
    {
        Assert.Equal(filter, TargetFrameworkPolicy.ValidateJitFilter(filter));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("*:*")]
    [InlineData("Calculator")]
    [InlineData("Calculator:Sum;cat /etc/passwd")]
    [InlineData("Calculator:$(danger)")]
    [InlineData("Calculator:Sum\nOther")]
    public void RejectsUnboundedOrUnsafeSelectors(string filter)
    {
        Assert.Throws<ArgumentException>(() =>
            TargetFrameworkPolicy.ValidateJitFilter(filter));
    }

    [Fact]
    public void GeneratesNet8JitHarnessWithoutHostExecution()
    {
        var root = Path.Combine(Path.GetTempPath(), "tenka-jit-target-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Demo.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        ExperimentReceipt? receipt = null;
        try
        {
            var store = new ExperimentStore(new WorkspaceGuard([root], false, false));
            receipt = store.Create(root, "Demo.csproj", "jit",
                "public static class Program { public static int Main() => 0; }",
                jitDisasmFilter: "Program:Main");
            var (manifest, dir) = store.Read(receipt.Id);
            Assert.Equal("jit", manifest.Kind);
            Assert.Equal("net8.0", manifest.TargetFramework);
            Assert.Equal("Program:Main", manifest.JitFilter);
            Assert.Equal("net8.0", receipt.TargetFramework);
            Assert.True(File.Exists(Path.Combine(dir, "GeneratedJit.csproj")));
            Assert.Contains("<TargetFramework>net8.0</TargetFramework>",
                File.ReadAllText(Path.Combine(dir, "GeneratedJit.csproj")));
            Assert.Throws<ArgumentException>(() => store.Create(root, "Demo.csproj",
                "jit", "public class Sample {}", jitDisasmFilter: "*"));
            Assert.Throws<ArgumentException>(() => store.Create(root, "Demo.csproj",
                "test", "public class Sample {}", jitDisasmFilter: "Program:Main"));
        }
        finally
        {
            if (receipt is not null && Directory.Exists(receipt.Directory))
                Directory.Delete(receipt.Directory, recursive: true);
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WithProject(string xml, Action<string> act)
    {
        var file = Path.Combine(Path.GetTempPath(),
            "tenka-tfm-" + Guid.NewGuid().ToString("N") + ".csproj");
        File.WriteAllText(file, xml);
        try { act(file); }
        finally { File.Delete(file); }
    }
}
