using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TenkaChan.Mcp.Analysis;
using Xunit;

namespace TenkaChan.Tests;

public sealed class ReviewMetadataFingerprintTests
{
    private static PortableExecutableReference Reference(string path) => MetadataReference.CreateFromImage(
        ImmutableArray.CreateRange(File.ReadAllBytes(typeof(object).Assembly.Location)), filePath: path);

    [Fact]
    public void TheSameLoadedModuleAtDifferentDisplayPathsHasTheSameIdentity()
    {
        var a = Reference(Path.GetFullPath("cache-a/System.Private.CoreLib.dll"));
        var b = Reference(Path.GetFullPath("cache-b/System.Private.CoreLib.dll"));
        Assert.NotEqual(a.Display, b.Display);
        var first = ReviewMetadataFingerprint.Read(a); var second = ReviewMetadataFingerprint.Read(b);
        Assert.True(first.Complete); Assert.True(second.Complete);
        Assert.Equal(first.Identity, second.Identity);
    }
    [Fact]
    public void AReferenceWithoutAFilePathStillHasMetadataIdentity()
    {
        var reference = MetadataReference.CreateFromImage(ImmutableArray.CreateRange(File.ReadAllBytes(typeof(object).Assembly.Location)));
        Assert.True(ReviewMetadataFingerprint.Read(reference).Complete);
    }
    [Fact]
    public void DifferentModulesCannotBeMergedByFileName()
    {
        var path = Path.GetFullPath("same-name.dll");
        var first = Reference(path);
        var second = MetadataReference.CreateFromImage(ImmutableArray.CreateRange(
            File.ReadAllBytes(typeof(ReviewMetadataFingerprint).Assembly.Location)), filePath: path);
        Assert.NotEqual(ReviewMetadataFingerprint.Read(first).Identity, ReviewMetadataFingerprint.Read(second).Identity);
    }
    [Fact]
    public void AliasesAffectTheIdentityButTheirOrderDoesNot()
    {
        var reference = Reference(Path.GetFullPath("core.dll"));
        var a = reference.WithAliases(ImmutableArray.Create("global", "A", "B"));
        var b = reference.WithAliases(ImmutableArray.Create("B", "A", "global"));
        Assert.NotEqual(ReviewMetadataFingerprint.Read(reference).Identity, ReviewMetadataFingerprint.Read(a).Identity);
        Assert.Equal(ReviewMetadataFingerprint.Read(a).Identity, ReviewMetadataFingerprint.Read(b).Identity);
    }
    [Fact]
    public void EmbedInteropTypesAffectsTheIdentity()
    {
        var reference = Reference(Path.GetFullPath("core.dll"));
        Assert.NotEqual(ReviewMetadataFingerprint.Read(reference).Identity,
            ReviewMetadataFingerprint.Read(reference.WithEmbedInteropTypes(true)).Identity);
    }
    [Fact]
    public void UnsupportedCompilationReferencesAreExplicitlyIncomplete()
    {
        var reference = CSharpCompilation.Create("in-memory").ToMetadataReference();
        var fingerprint = ReviewMetadataFingerprint.Read(reference);
        Assert.False(fingerprint.Complete);
        Assert.Contains("unresolved", fingerprint.Identity);
    }
}
