using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Experiments;

public sealed class JitBinarySourceService(ExperimentStore store, WorkspaceGuard sourceGuard)
{
    public IReadOnlyList<NativeBinaryArtifact> Files(string experimentId)
    {
        var (_, report) = Open(experimentId);
        return report.BinaryArtifacts;
    }

    public BinaryMethodCatalog Methods(string experimentId, string assemblyFile,
        string query = "", int limit = 100)
    {
        var (directory, report) = Open(experimentId);
        var path = NativeBinaryArtifacts.Verify(directory, report.BinaryArtifacts, assemblyFile);
        var inspector = new BinarySourceInspector(new WorkspaceGuard([directory], false, false));
        var result = inspector.Methods(path, query, limit);
        if (result.AssemblySha256 != report.BinaryArtifacts.Single(a => a.FileName == assemblyFile).Sha256)
            throw new InvalidDataException("Assembly changed during inspection.");
        return result;
    }

    public BinarySourceEvidence Inspect(string experimentId, string assemblyFile,
        int methodToken, string? sourceFilePath = null, int maxInstructions = 2000)
    {
        var (directory, report) = Open(experimentId);
        var path = NativeBinaryArtifacts.Verify(directory, report.BinaryArtifacts, assemblyFile);
        var pdbName = Path.ChangeExtension(assemblyFile, ".pdb");
        string? pdb = null;
        var pdbRecord = report.BinaryArtifacts.SingleOrDefault(a => a.FileName == pdbName);
        if (pdbRecord is not null) pdb = NativeBinaryArtifacts.Verify(directory, report.BinaryArtifacts, pdbName);
        else if (File.Exists(Path.Combine(directory, pdbName)))
            throw new InvalidDataException("An unrecorded PDB appeared after execution.");
        string? source = sourceFilePath is null ? null : sourceGuard.RequireFile(sourceFilePath, ".cs");
        var roots = source is null ? new[] { directory } : new[] { directory, Path.GetDirectoryName(source)! };
        var inspector = new BinarySourceInspector(new WorkspaceGuard(roots, false, false));
        var result = inspector.Inspect(path, methodToken, pdb, source, maxInstructions);
        if (result.AssemblySha256 != report.BinaryArtifacts.Single(a => a.FileName == assemblyFile).Sha256 ||
            (pdbRecord is not null && result.PdbSha256 != pdbRecord.Sha256))
            throw new InvalidDataException("Captured binary/PDB changed during inspection.");
        return result;
    }

    private (string Directory, ExecutionResult Report) Open(string id)
    {
        var (manifest, directory) = store.Read(id);
        var report = store.ReadExecutionResult(id);
        if (manifest.Kind != "jit" || report.Kind != "jit" || report.Status != "jit_completed" || report.ExperimentId != id)
            throw new InvalidOperationException("Use a completed JIT execution with captured build artifacts.");
        var artifacts = Path.Combine(directory, "artifacts");
        new WorkspaceGuard([artifacts], false, false).RequireDirectory(artifacts);
        return (artifacts, report);
    }
}
