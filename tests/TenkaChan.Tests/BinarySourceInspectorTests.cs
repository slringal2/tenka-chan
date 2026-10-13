using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class BinarySourceInspectorTests
{
    private const string Code = """
        using System;
        using System.Collections.Generic;
        public static class Sample
        {
            public static object Box(int value) { return value; }
            public static object Box(long value) { return value; }
            public static int Work(int x)
            {
                var list = new List<int>();
                if (x > 0) list.Add(x);
                else list.Add(-1);
                return list.Count;
            }
            public static int Hidden(int x)
            {
        #line hidden
                object boxed = x;
        #line default
                return boxed.GetHashCode();
            }
        }
        """;

    [Fact]
    public void EmitsRealPeAndPdbAndResolvesOverloadsCallsAndSourceRanges()
    {
        using var fixture = new Fixture();
        var inspector = fixture.Inspector;
        var catalog = inspector.Methods(fixture.Assembly);
        Assert.Equal(64, catalog.AssemblySha256.Length);
        Assert.True(Guid.TryParse(catalog.ModuleVersionId, out _));
        var box = Assert.Single(catalog.Methods, m => m.Name == "Box" && m.Signature.Contains("(int)"));
        var report = inspector.Inspect(fixture.Assembly, box.MetadataToken, sourceFilePath: fixture.Source);
        Assert.Equal("verified_external_portable_pdb", report.PdbStatus);
        Assert.Equal(catalog.AssemblySha256, report.AssemblySha256);
        var boxing = Assert.Single(report.Instructions, i => i.OpCode == "box");
        Assert.Contains("Int32", boxing.Operand);
        Assert.NotNull(boxing.ReferencedToken);
        Assert.Equal("pdb_sequence_point_range", boxing.SourceMappingStatus);
        Assert.Equal("checksum_verified", boxing.Source!.LocalSourceStatus);
        Assert.Contains("return value", boxing.Source.VerifiedSourceExcerpt);
        Assert.Contains(report.CostSites, s => s.Kind == "il_boxing" && s.Source is not null);
        var work = Assert.Single(catalog.Methods, m => m.Name == "Work");
        var calls = inspector.Inspect(fixture.Assembly, work.MetadataToken, sourceFilePath: fixture.Source);
        Assert.Contains(calls.Instructions, i => i.Operand.Contains("::Add("));
        Assert.Contains(calls.Instructions, i => i.BranchTargets.Count > 0);
        var starts = calls.Instructions.Select(i => i.Offset).ToHashSet();
        Assert.All(calls.Instructions.SelectMany(i => i.BranchTargets), target => Assert.Contains(target, starts));
    }

    [Fact]
    public void RefusesPdbFromAnotherBuild()
    {
        using var first = new Fixture();
        using var other = new Fixture(Code.Replace("return value;", "return value + 2;", StringComparison.Ordinal));
        var all = new BinarySourceInspector(new WorkspaceGuard([first.Directory, other.Directory], false, false));
        var token = first.Inspector.Methods(first.Assembly).Methods[0].MetadataToken;
        Assert.Throws<InvalidDataException>(() => all.Inspect(first.Assembly, token, other.Pdb));
    }

    [Fact]
    public void DoesNotPresentChangedSourceAsMatchingMachineCode()
    {
        using var fixture = new Fixture();
        var token = fixture.Inspector.Methods(fixture.Assembly).Methods[0].MetadataToken;
        File.AppendAllText(fixture.Source, "\n// edited after compile\n");
        var report = fixture.Inspector.Inspect(fixture.Assembly, token, sourceFilePath: fixture.Source);
        var mapped = report.Instructions.Where(i => i.Source is not null).ToArray();
        Assert.NotEmpty(mapped);
        Assert.All(mapped, i =>
        {
            Assert.Equal("checksum_mismatch", i.Source!.LocalSourceStatus);
            Assert.Null(i.Source.VerifiedSourceExcerpt);
        });
    }

    [Fact]
    public void KeepsHiddenSequencePointRangesUnmapped()
    {
        using var fixture = new Fixture();
        var method = Assert.Single(fixture.Inspector.Methods(fixture.Assembly).Methods, m => m.Name == "Hidden");
        var report = fixture.Inspector.Inspect(fixture.Assembly, method.MetadataToken, sourceFilePath: fixture.Source);
        Assert.Contains(report.Instructions, i => i.SourceMappingStatus == "hidden_sequence_point" && i.Source is null);
    }

    [Fact]
    public void SupportsEmbeddedPortablePdbAndMissingSymbols()
    {
        using var embedded = new Fixture(embedded: true);
        var token = embedded.Inspector.Methods(embedded.Assembly).Methods[0].MetadataToken;
        var withSymbols = embedded.Inspector.Inspect(embedded.Assembly, token, sourceFilePath: embedded.Source);
        Assert.Equal("verified_embedded_portable_pdb", withSymbols.PdbStatus);
        Assert.Contains(withSymbols.Instructions, i => i.Source is not null);
        using var external = new Fixture();
        File.Delete(external.Pdb);
        token = external.Inspector.Methods(external.Assembly).Methods[0].MetadataToken;
        var missing = external.Inspector.Inspect(external.Assembly, token);
        Assert.Equal("missing_pdb", missing.PdbStatus);
        Assert.All(missing.Instructions, i => Assert.Null(i.Source));
    }

    [Fact]
    public void RejectsBadMethodTokensAndDisclosesTruncation()
    {
        using var fixture = new Fixture();
        Assert.Throws<ArgumentException>(() => fixture.Inspector.Inspect(fixture.Assembly, 1));
        Assert.Throws<ArgumentException>(() => fixture.Inspector.Inspect(fixture.Assembly, 0x06FFFFFF));
        var method = Assert.Single(fixture.Inspector.Methods(fixture.Assembly).Methods, m => m.Name == "Work");
        var partial = fixture.Inspector.Inspect(fixture.Assembly, method.MetadataToken, maxInstructions: 1);
        Assert.Single(partial.Instructions);
        Assert.True(partial.Truncated);
        Assert.True(partial.TotalInstructions > 1);
        Assert.True(fixture.Inspector.Methods(fixture.Assembly, limit: 1).Truncated);
    }

    [Fact]
    public void DoesNotReadSourceOrPdbOutsideAllowedRoots()
    {
        using var fixture = new Fixture();
        using var other = new Fixture();
        var token = fixture.Inspector.Methods(fixture.Assembly).Methods[0].MetadataToken;
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Inspector.Inspect(fixture.Assembly, token, other.Pdb));
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Inspector.Inspect(fixture.Assembly, token, sourceFilePath: other.Source));
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "tenka-symbols-" + Guid.NewGuid().ToString("N"));
        public string Assembly => Path.Combine(Directory, "Subject.dll");
        public string Pdb => Path.Combine(Directory, "Subject.pdb");
        public string Source => Path.Combine(Directory, "Subject.cs");
        public BinarySourceInspector Inspector => new(new WorkspaceGuard([Directory], false, false));
        public Fixture(string source = Code, bool embedded = false)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var encoding = new UTF8Encoding(false);
            File.WriteAllText(Source, source, encoding);
            var tree = CSharpSyntaxTree.ParseText(SourceText.From(source, encoding, SourceHashAlgorithm.Sha256), path: Source);
            var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create("Subject", [tree], refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                    optimizationLevel: OptimizationLevel.Release, deterministic: true));
            using var pe = File.Create(Assembly);
            using var pdb = embedded ? null : File.Create(Pdb);
            var result = compilation.Emit(pe, pdb, options: new EmitOptions(
                debugInformationFormat: embedded ? DebugInformationFormat.Embedded : DebugInformationFormat.PortablePdb));
            Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, true);
    }
}
