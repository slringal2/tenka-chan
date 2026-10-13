using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using TenkaChan.Mcp.Experiments;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Tools;

[McpServerToolType]
public static class JitBinarySourceTools
{
    [McpServerTool(Name = "jit_binary_files")]
    [Description("List hashed DLL/PDB build artifacts captured in a completed JIT run. These are execution-specific evidence, not later host build output.")]
    public static string Files(ExperimentStore store, WorkspaceGuard guard, string experimentId) =>
        JsonSerializer.Serialize(new JitBinarySourceService(store, guard).Files(experimentId));

    [McpServerTool(Name = "jit_binary_methods")]
    [Description("Verify the captured binary hash and list method metadata tokens/overload signatures for this JIT run. assemblyFile is a fileName from jit_binary_files, not a path.")]
    public static string Methods(ExperimentStore store, WorkspaceGuard guard, string experimentId,
        string assemblyFile, string query = "", int limit = 100) =>
        JsonSerializer.Serialize(new JitBinarySourceService(store, guard).Methods(experimentId, assemblyFile, query, limit));

    [McpServerTool(Name = "jit_source_inspect")]
    [Description("Decode IL and matched Portable PDB source ranges from the DLL actually captured during the selected JIT experiment. Optional local source must pass the original path allowlist and PDB checksum before its text is shown. This does not invent native-to-IL offsets.")]
    public static string Inspect(ExperimentStore store, WorkspaceGuard guard, string experimentId,
        string assemblyFile, int methodToken, string? sourceFilePath = null, int maxInstructions = 2000) =>
        JsonSerializer.Serialize(new JitBinarySourceService(store, guard).Inspect(experimentId,
            assemblyFile, methodToken, sourceFilePath, maxInstructions));
}
