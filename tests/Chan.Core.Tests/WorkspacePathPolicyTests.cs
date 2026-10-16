using Chan.Core.Security;
using Xunit;

namespace Chan.Core.Tests;

public sealed class WorkspacePathPolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "chan-core-" + Guid.NewGuid().ToString("N"));

    public WorkspacePathPolicyTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);
    private WorkspacePathPolicy Policy() => new([_root]);

    [Fact]
    public void EmptyAllowlistFailsClosed() =>
        Assert.Throws<UnauthorizedAccessException>(() => new WorkspacePathPolicy([]).RequireDirectory(_root));

    [Fact]
    public void NormalizesAllowedDirectory()
    {
        Directory.CreateDirectory(Path.Combine(_root, "child"));
        var input = Path.Combine(_root, "child", "..", ".");
        Assert.Equal(Path.GetFullPath(input), Policy().RequireDirectory(input));
    }

    [Theory]
    [InlineData(".csproj")]
    [InlineData(".vcxproj")]
    [InlineData(".cpp")]
    public void AcceptsCallerSelectedFileTypes(string extension)
    {
        var file = Path.Combine(_root, "Example" + extension);
        File.WriteAllText(file, "fixture");
        Assert.Equal(file, Policy().RequireFile(file, extension.ToUpperInvariant()));
    }

    [Fact]
    public void RejectsSiblingWithSamePrefix() =>
        Assert.Throws<UnauthorizedAccessException>(() => Policy().RequireDirectory(_root + "-other"));

    [Fact]
    public void RejectsTraversalOutsideAllowlist() =>
        Assert.Throws<UnauthorizedAccessException>(() =>
            Policy().RequireDirectory(Path.Combine(_root, "..", "outside")));

    [Fact]
    public void PreservesExtensionAndExistenceChecks()
    {
        var file = Path.Combine(_root, "missing.cpp");
        Assert.Throws<ArgumentException>(() => Policy().RequireFile(file, ".csproj"));
        Assert.Throws<FileNotFoundException>(() => Policy().RequireFile(file, ".cpp"));
        Assert.Throws<DirectoryNotFoundException>(() => Policy().RequireDirectory(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void ContainmentIsSegmentAwareAndDoesNotClaimExistence()
    {
        var policy = Policy();
        Assert.True(policy.IsWithin(_root, _root));
        Assert.True(policy.IsWithin(_root, Path.Combine(_root, "not-created.cpp")));
        Assert.False(policy.IsWithin(_root, _root + "-other"));
        Assert.False(policy.IsWithin(_root, Path.Combine(_root, "..", "elsewhere")));
    }

    [Fact]
    public void DiagnosticsCanNameTheHostSetting()
    {
        var policy = new WorkspacePathPolicy([], "APP_ALLOWED_ROOTS");
        var error = Assert.Throws<UnauthorizedAccessException>(() => policy.RequireDirectory(_root));
        Assert.Contains("APP_ALLOWED_ROOTS", error.Message);
    }

    [NonWindowsSymlinkFact]
    public void RejectsLinkedDirectoriesAndLinkedAncestors()
    {
        var target = Directory.CreateDirectory(Path.Combine(_root, "target")).FullName;
        File.WriteAllText(Path.Combine(target, "source.cpp"), "fixture");
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, target);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => Policy().RequireDirectory(link));
            Assert.Throws<UnauthorizedAccessException>(() =>
                Policy().RequireFile(Path.Combine(link, "source.cpp"), ".cpp"));
        }
        finally { Directory.Delete(link); }
    }
}

public sealed class NonWindowsSymlinkFactAttribute : FactAttribute
{
    public NonWindowsSymlinkFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Creating Windows symlinks needs privileges/Developer Mode; run the reparse-point checks on a prepared Windows host.";
    }
}
