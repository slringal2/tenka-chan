using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class PerformanceInvestigationIntegrationTests
{
    private const string Code = """
        namespace App;
        public static class Work
        {
            public static int Hot(int x) => x + 1;
            public static int Cold(int x) => x - 1;
        }
        """;

    [Fact]
    public async Task WorkloadPriorityAndDossierUseCurrentWorkspaceAndResetAfterReload()
    {
        using var fixture = new Fixture();
        using var roslyn = new RoslynReviewService(fixture.Guard);
        await roslyn.OpenAsync(fixture.Project);
        var hot = Assert.Single(roslyn.FindMethods("Hot"));
        await roslyn.ImportHotspotProfileAsync(fixture.Profile, maxFrames: 1);
        var service = new PerformanceInvestigationService(roslyn, fixture.Store, fixture.Guard);
        var triage = await service.TriageAsync();
        Assert.Equal("profile_guided", triage.Status);
        Assert.Equal(hot.Id, triage.Entries[0].MethodId);
        Assert.Equal(80, triage.Entries[0].ExclusivePercent);
        Assert.Equal(2, triage.ObservedMethods);
        Assert.True(triage.ProfileFramesTruncated);
        var detail = await service.InvestigateAsync(hot.Id);
        Assert.Contains("x + 1", detail.Method.Source);
        Assert.Equal(hot.Id, detail.Profile!.MethodCost!.MethodId);
        Assert.Null(detail.CompiledEvidence);
        Assert.Equal("qualified_name_only_build_unverified", detail.Profile.Association);
        var initial = triage.SnapshotId;
        await roslyn.OpenAsync(fixture.Project);
        var next = await service.TriageAsync();
        Assert.NotEqual(initial, next.SnapshotId);
        Assert.Equal("static_only", next.Status);
        Assert.Null(next.ProfileSha256);
    }

    [Fact]
    public async Task CapturedBinaryDossierRetainsSeparateIdentitiesAndRejectsWrongMethodAndTampering()
    {
        using var fixture = new Fixture();
        using var roslyn = new RoslynReviewService(fixture.Guard);
        await roslyn.OpenAsync(fixture.Project);
        var hot = Assert.Single(roslyn.FindMethods("Hot"));
        var receipt = fixture.PrepareSyntheticExecution();
        var binaries = new JitBinarySourceService(fixture.Store, fixture.Guard);
        var hotBinary = Assert.Single(binaries.Methods(receipt.Id, "binary-Demo.dll", "::Hot(").Methods);
        var coldBinary = Assert.Single(binaries.Methods(receipt.Id, "binary-Demo.dll", "::Cold(").Methods);
        var service = new PerformanceInvestigationService(roslyn, fixture.Store, fixture.Guard);
        var report = await service.InvestigateAsync(hot.Id, receipt.Id, "binary-Demo.dll", hotBinary.MetadataToken);
        Assert.NotNull(report.CompiledEvidence);
        Assert.Equal("verified_external_portable_pdb", report.CompiledEvidence.IlAndSource.PdbStatus);
        Assert.Contains(report.CompiledEvidence.IlAndSource.Instructions,
            i => i.Source?.LocalSourceStatus == "checksum_verified");
        Assert.Single(report.CompiledEvidence.NativeMethods);
        Assert.Contains(report.CompiledEvidence.Associations, a => a.Contains("NOT proof", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.InvestigateAsync(hot.Id,
            receipt.Id, "binary-Demo.dll", coldBinary.MetadataToken));
        await Assert.ThrowsAsync<ArgumentException>(() => service.InvestigateAsync(hot.Id, receipt.Id));
        File.AppendAllText(fixture.Source, "\n// changed since build");
        var stale = await service.InvestigateAsync(hot.Id, receipt.Id, "binary-Demo.dll", hotBinary.MetadataToken);
        Assert.Contains(stale.CompiledEvidence!.IlAndSource.Instructions,
            i => i.Source?.LocalSourceStatus == "checksum_mismatch");
        Assert.All(stale.CompiledEvidence.IlAndSource.Instructions.Where(i => i.Source is not null),
            i => Assert.Null(i.Source!.VerifiedSourceExcerpt));
        File.AppendAllText(Path.Combine(receipt.Directory, "artifacts", "binary-Demo.dll"), "tamper");
        await Assert.ThrowsAsync<InvalidDataException>(() => service.InvestigateAsync(hot.Id,
            receipt.Id, "binary-Demo.dll", hotBinary.MetadataToken));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tenka-investigate-" + Guid.NewGuid().ToString("N"));
        public string Project => Path.Combine(Root, "Demo.csproj");
        public string Source => Path.Combine(Root, "Work.cs");
        public string Profile => Path.Combine(Root, "sample.json");
        public WorkspaceGuard Guard { get; }
        public ExperimentStore Store { get; }
        private readonly List<string> _experimentDirectories = [];
        public Fixture()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(Source, Code, new UTF8Encoding(false));
            File.WriteAllText(Profile, """
                {"shared":{"frames":[{"name":"App.Work.Hot(int)"},{"name":"App.Work.Cold(int)"}]},
                 "profiles":[{"type":"sampled","name":"synthetic-test","unit":"milliseconds",
                  "startValue":0,"endValue":10,"samples":[[0],[1]],"weights":[8,2]}]}
                """);
            Guard = new WorkspaceGuard([Root], true, false);
            Store = new ExperimentStore(Guard);
        }
        public ExperimentReceipt PrepareSyntheticExecution()
        {
            var receipt = Store.Create(Root, "Demo.csproj", "jit",
                "public static class Entry { public static int Main() => 0; }", jitDisasmFilter: "App.Work:Hot");
            _experimentDirectories.Add(receipt.Directory);
            var artifacts = Store.CreateArtifactsDirectory(receipt.Id);
            var tree = CSharpSyntaxTree.ParseText(SourceText.From(Code, new UTF8Encoding(false), SourceHashAlgorithm.Sha256), path: Source);
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Select(path => MetadataReference.CreateFromFile(path)).ToArray();
            var compilation = CSharpCompilation.Create("Demo", [tree], references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
            using (var pe = File.Create(Path.Combine(artifacts, "binary-Demo.dll")))
            using (var pdb = File.Create(Path.Combine(artifacts, "binary-Demo.pdb")))
            {
                var emitted = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
                Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics));
            }
            // Synthetic execution metadata validates only the evidence join.
            // The separate Docker job verifies actual .NET8 JIT execution.
            var native = NativeJitDisassemblyParser.Parse("""
                ; Assembly listing for method App.Work:Hot(int):int (FullOpts)
                ; Emitting BLENDED_CODE for X64
                       33C0 xor eax, eax
                       C3 ret
                ; Total bytes of code 3
                """, "net10.0", "fixture-image", "App.Work:Hot");
            Store.SaveExecutionResult(new ExecutionResult(receipt.Id, "jit", "jit_completed", 0,
                "test-start", "test-end", null, [], [], "", "", "Synthetic test metadata")
            {
                TargetFramework = "net10.0", NativeDisassembly = native,
                BinaryArtifacts = NativeBinaryArtifacts.Snapshot(artifacts)
            });
            return receipt;
        }
        public void Dispose()
        {
            foreach (var dir in _experimentDirectories)
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }
}
