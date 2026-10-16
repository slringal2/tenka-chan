using System.Text;
using TenkaChan.Mcp.Agent;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using TenkaChan.Mcp.Tracking;
using Xunit;

namespace TenkaChan.Tests;

public sealed class ChangeProposalSafetyTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "tenka-proposal-" + Guid.NewGuid().ToString("N"));
    private string Root => Path.Combine(_home, "source");
    private string FilePath => Path.Combine(Root, "C.cs");
    private readonly ChangeProposalStore _store;
    private const string Old = "public class C { public int Count() => 1; }\n";
    private const string New = "public class C { public int Count() => 2; }\n";
    public ChangeProposalSafetyTests()
    {
        Directory.CreateDirectory(Root); File.WriteAllText(FilePath, Old, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(Root, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        _store = new ChangeProposalStore(new WorkspaceGuard([Root], true, false), Path.Combine(_home, "data"));
    }
    private ReviewTarget Target() => new("target", "selection", "method", "p", "App", Path.Combine(Root, "App.csproj"), "Count", FilePath, 1,
        ReviewProgress.Hash(Old), "context", true, null, Old);
    private ChangeProposal Create(string replacement = New, string? expected = null) => _store.Create(Guid.NewGuid().ToString("N"), "snapshot", Target(), Root,
        expected ?? InvestigationFiles.Hash(File.ReadAllBytes(FilePath)), replacement, [], default);
    private static BenchmarkObservation Bench(double mean = 100) => new("Case", "Type", "Method", "", 5, mean, mean, 16, "same-host");
    private static ProposalValidation Good(ChangeProposal p) => new(p.Id, p.AfterHash, "Tests.csproj", "Bench.csproj", "*", "validation_completed",
        [new("baseline_test", "completed", 0, new(1, 1, 0, 0, "Completed", []), [], "", ""),
         new("candidate_test", "completed", 0, new(1, 1, 0, 0, "Completed", []), [], "", ""),
         new("baseline_benchmark", "completed", 0, null, [Bench()], "", ""),
         new("candidate_benchmark", "completed", 0, null, [Bench(80)], "", "")], [], "Unit fixture, not an actual benchmark run.");

    [Fact] public void PreparingCopiesDoesNotTouchOriginalAndCopiesAreIndependent()
    {
        var p = Create(); Assert.Equal(Old, File.ReadAllText(FilePath));
        Assert.Equal(Old, File.ReadAllText(Path.Combine(_store.CopyRoot(p.Id, "baseline"), "C.cs")));
        Assert.Equal(New, File.ReadAllText(Path.Combine(_store.CopyRoot(p.Id, "candidate"), "C.cs")));
        _store.VerifyInputs(p, _store.CopyRoot(p.Id, "baseline"), false, default);
        _store.VerifyInputs(p, _store.CopyRoot(p.Id, "candidate"), true, default);
    }
    [Fact] public void StaleFileHashAndInvalidSyntaxFailBeforePublishing()
    {
        Assert.Throws<ReviewConflictException>(() => Create(expected: "stale"));
        Assert.Throws<ArgumentException>(() => Create("not valid C# {")); Assert.Equal(Old, File.ReadAllText(FilePath));
    }
    [Fact] public void ApplyAndRevertNeedSeparateApprovalAndRestoreExactBytes()
    {
        var before = File.ReadAllBytes(FilePath); var p = Create(); _store.SaveValidation(Good(p));
        Assert.Throws<UnauthorizedAccessException>(() => _store.Replace(p.Id, false, false, default));
        _store.Replace(p.Id, false, true, default); Assert.Equal(New, File.ReadAllText(FilePath));
        Assert.Throws<UnauthorizedAccessException>(() => _store.Replace(p.Id, true, false, default));
        _store.Replace(p.Id, true, true, default); Assert.Equal(before, File.ReadAllBytes(FilePath));
        Assert.NotNull(_store.Read(p.Id).BackupPath);
    }
    [Fact] public void NoReportAndEmptySuccessMarkerDoNotAuthorizeApply()
    {
        var p = Create(); Assert.ThrowsAny<IOException>(() => _store.Replace(p.Id, false, true, default));
        _store.SaveValidation(Good(p) with { Stages = [] });
        Assert.Throws<InvalidOperationException>(() => _store.Replace(p.Id, false, true, default)); Assert.Equal(Old, File.ReadAllText(FilePath));
    }
    [Fact] public void ExternalEditPreventsApplyAndIsNotOverwritten()
    {
        var p = Create(); _store.SaveValidation(Good(p)); File.WriteAllText(FilePath, "public class External { }\n");
        Assert.Throws<InvalidOperationException>(() => _store.Replace(p.Id, false, true, default));
        Assert.Contains("External", File.ReadAllText(FilePath));
    }
    [Fact] public void LaterEditPreventsRevert()
    {
        var p = Create(); _store.SaveValidation(Good(p)); _store.Replace(p.Id, false, true, default);
        File.AppendAllText(FilePath, "// later edit\n");
        Assert.Throws<ReviewConflictException>(() => _store.Replace(p.Id, true, true, default)); Assert.Contains("later edit", File.ReadAllText(FilePath));
    }
    [Fact] public void ChangedCompanionInputInvalidatesTheFrozenCopy()
    {
        var p = Create(); File.AppendAllText(Path.Combine(_store.CopyRoot(p.Id, "candidate"), "App.csproj"), " ");
        Assert.Throws<InvalidOperationException>(() => _store.VerifyInputs(p, _store.CopyRoot(p.Id, "candidate"), true, default));
    }
    [Fact] public void AddedSourceCannotBeIgnoredButBuildOutputsAreExcluded()
    {
        var p = Create(); var root = _store.CopyRoot(p.Id, "baseline");
        Directory.CreateDirectory(Path.Combine(root, "obj")); File.WriteAllText(Path.Combine(root, "obj", "generated.cs"), "generated");
        _store.VerifyInputs(p, root, false, default);
        File.WriteAllText(Path.Combine(root, "Added.cs"), "class Added {}");
        Assert.Throws<InvalidOperationException>(() => _store.VerifyInputs(p, root, false, default));
    }
    [Theory]
    [InlineData("../outside.cs")]
    [InlineData("C:/outside.cs")]
    public void RelativePathsCannotEscapeCopies(string path) => Assert.ThrowsAny<Exception>(() => ChangeProposalStore.Within(Root, path));
    [Fact] public void DynamicOrExternalProjectReferencesAreRejectedBeforeCopyExecution()
    {
        File.WriteAllText(Path.Combine(Root, "App.csproj"), "<Project><ItemGroup><ProjectReference Include=\"../Outside.csproj\" /></ItemGroup></Project>");
        Assert.Throws<InvalidOperationException>(() => Create()); Assert.Equal(Old, File.ReadAllText(FilePath));
    }
    [Fact] public void UnfinishedReplacementNeedsExplicitRecoveryNotBlindRetry()
    {
        var p = Create(); _store.Save(p with { Status = "applying" });
        Assert.Throws<InvalidOperationException>(() => _store.Replace(p.Id, false, true, default)); Assert.Equal(Old, File.ReadAllText(FilePath));
    }
    [Fact] public void EncodingAndNewlineStyleArePreserved()
    {
        var bytes = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(Old.Replace("\n", "\r\n"))).ToArray();
        var encoded = ChangeProposalStore.EncodeLike(bytes, New);
        Assert.True(encoded.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        Assert.Equal(New.Replace("\n", "\r\n"), ChangeProposalStore.Decode(encoded));
    }
    public void Dispose() { if (Directory.Exists(_home)) Directory.Delete(_home, true); }
}
