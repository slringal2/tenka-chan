using System.Text.Json;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;
using Xunit;
namespace TenkaChan.Tests;
public sealed class NativeInstructionAnalyzerDiagnosticsTests
{
    [Fact]
    public async Task PreserveNativeCaptureForCrossPlatformDecoderDiagnosis()
    {
        var fixture=Environment.GetEnvironmentVariable("TENKA_ASSEMBLY_SMOKE_DLL");
        var output=Environment.GetEnvironmentVariable("TENKA_ASSEMBLY_TEST_REPORT");
        if(string.IsNullOrWhiteSpace(fixture)||string.IsNullOrWhiteSpace(output)) return;
        var runner=new TrustedLocalJitRunner(new WorkspaceGuard([Path.GetDirectoryName(fixture)!],false,false),true);
        var capture=await runner.CaptureAsync(fixture,"LocalJitFixture:Compute",true,45);
        await File.WriteAllTextAsync(output,JsonSerializer.Serialize(new {capture,listing=File.ReadAllText(capture.NativeListingPath)}));
        Assert.Equal("jit_completed",capture.Status);
    }
}
