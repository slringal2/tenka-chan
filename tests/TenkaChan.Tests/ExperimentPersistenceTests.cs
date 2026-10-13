using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;

namespace TenkaChan.Tests;

public sealed class ExperimentPersistenceTests
{
    [Fact]
    public void StoresVerifiedResultAndRestoresWithoutExecution()
    {
        WithPreparedStore((store, receipt) =>
        {
            var report = new ExecutionResult(
                receipt.Id, "test", "tests_failed", 1, "2026-10-01T00:00:00Z",
                "2026-10-01T00:00:10Z",
                new TestRunSummary(2, 1, 1, 0, "Failed",
                    [new TestCaseObservation("BadValue", "Failed", "bad")]),
                [], ["results.trx"], "output", "errors", "From TRX");
            store.SaveExecutionResult(report);
            var saved = store.ReadExecutionResult(receipt.Id);
            Assert.Equal("tests_failed", saved.Status);
            Assert.Equal(1, saved.Tests?.Failed);
            Assert.Equal("BadValue", Assert.Single(saved.Tests!.Failures).Name);
            var path = store.CreateArtifactsDirectory(receipt.Id);
            Assert.True(Directory.Exists(path));
        });
    }

    [Fact]
    public void RejectsTamperingWithGeneratedSource()
    {
        WithPreparedStore((store, receipt) =>
        {
            File.AppendAllText(Path.Combine(receipt.Directory, "Generated.cs"),
                "\n// modified after preparation");
            Assert.Throws<InvalidDataException>(() => store.Read(receipt.Id));
        });
    }

    [Fact]
    public void RejectsTamperingWithGeneratedProject()
    {
        WithPreparedStore((store, receipt) =>
        {
            File.AppendAllText(Path.Combine(receipt.Directory, "GeneratedTests.csproj"),
                "\n<!-- malicious build change -->");
            Assert.Throws<InvalidDataException>(() => store.Read(receipt.Id));
        });
    }

    private static void WithPreparedStore(Action<ExperimentStore, ExperimentReceipt> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "tenka-storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Sample.csproj"), "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        ExperimentReceipt? receipt = null;
        try
        {
            var store = new ExperimentStore(new WorkspaceGuard([root], false, false));
            receipt = store.Create(root, "Sample.csproj", "test",
                "using Xunit; public class Test { [Fact] public void Smoke() => Assert.True(true); }");
            test(store, receipt);
        }
        finally
        {
            if (receipt is not null && Directory.Exists(receipt.Directory))
                Directory.Delete(receipt.Directory, recursive: true);
            Directory.Delete(root, recursive: true);
        }
    }
}
