using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Tools;

[McpServerToolType]
public static class BinarySourceTools
{
    [McpServerTool(Name = "assembly_methods")]
    [Description("List method metadata tokens and complete overload signatures from an allowlisted DLL/EXE without executing it. Tokens belong only to the reported assembly SHA256/MVID.")]
    public static string Methods(WorkspaceGuard guard, string assemblyPath, string query = "", int limit = 100) =>
        JsonSerializer.Serialize(new BinarySourceInspector(guard).Methods(assemblyPath, query, limit));

    [McpServerTool(Name = "binary_source_inspect")]
    [Description("Inspect a specific MethodDef token: decode IL operands/branch targets and map verified Portable PDB sequence points to source spans. Source text is shown only after checksum verification. Never reads paths embedded in PDBs.")]
    public static string Inspect(WorkspaceGuard guard, string assemblyPath, int methodToken,
        string? pdbPath = null, string? sourceFilePath = null, int maxInstructions = 2000) =>
        JsonSerializer.Serialize(new BinarySourceInspector(guard).Inspect(assemblyPath, methodToken,
            pdbPath, sourceFilePath, maxInstructions));
}
