using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Tools;

[McpServerToolType]
public static class AssemblyWorkbenchTools
{
    [McpServerTool(Name = "assembly_native_analyze")]
    [Description("Read one explicit saved JIT body or allowlisted .txt listing, independently decode complete x86/x64 bytes with Iced, inspect CPUID/register/memory use and normal branch flow. Never executes code. Instruction counts and P2 sites are NOT measured cost or defects; ARM64 decoding is explicitly unsupported.")]
    public static string Analyze(WorkspaceGuard guard, ExperimentStore experiments,
        TrustedLocalJitRunner local, string? listingPath = null, string? jitExperimentId = null,
        string? localJitCaptureId = null, int methodIndex = 0) => JsonSerializer.Serialize(
            new AssemblyWorkbench(guard, experiments, local).Analyze(new(listingPath, jitExperimentId, localJitCaptureId, methodIndex)));

    [McpServerTool(Name = "assembly_native_compare")]
    [Description("Compare two explicitly selected native assembly bodies, retaining each input and limits. Reports code size/instruction/CPUID differences, never speedup, CPU latency or semantic equivalence.")]
    public static string Compare(WorkspaceGuard guard, ExperimentStore experiments,
        TrustedLocalJitRunner local, NativeAssemblySelection baseline, NativeAssemblySelection candidate) =>
        JsonSerializer.Serialize(new AssemblyWorkbench(guard, experiments, local).Compare(baseline, candidate));
}
