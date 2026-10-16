using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TenkaChan.Mcp.Agent;
using TenkaChan.Mcp.Security;
using TenkaChan.Mcp.Tracking;

namespace TenkaChan.Mcp.Experiments;

public sealed record ProposalInput(string Path, string Hash, long Bytes);
public sealed record ProposalCompanion(string Id, string Path, string Kind);
public sealed record ChangeProposal(string Id, string InvestigationId, string TargetId, string SnapshotId,
    string Root, string Project, string File, string SourceHash, string ContextHash,
    string BeforeHash, string AfterHash, string CreatedUtc, string Status,
    IReadOnlyList<ProposalInput> Inputs, IReadOnlyList<ProposalCompanion> Companions,
    string? BackupPath = null);

/// <summary>
/// Single EXISTING C# file proposals. Copies, validation and replacement are
/// separate operations. No source execution occurs in Create/Diff/Apply/Revert.
/// The host execution used for validation is explicitly NOT an OS sandbox.
/// </summary>
public sealed class ChangeProposalStore
{
    internal const int MaximumFiles = 8000, MaximumFileBytes = 16_000_000;
    internal const long MaximumTotalBytes = 128_000_000;
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    { ".git", ".vs", ".idea", "bin", "obj", "node_modules", ".tenka" };
    private readonly string _root;
    private readonly WorkspaceGuard _guard;
    private readonly ReviewDiffCache _diff = new();
    public ChangeProposalStore(WorkspaceGuard guard) : this(guard, InvestigationFiles.DataRoot) { }
    internal ChangeProposalStore(WorkspaceGuard guard, string dataRoot)
    { _guard = guard; _root = InvestigationFiles.DirectoryAt(Path.Combine(dataRoot, "proposals")); }
    internal string Folder(string id) => Path.Combine(_root, InvestigationFiles.Id(id));
    internal string CopyRoot(string id, string variant) => Path.Combine(Folder(id), variant is "baseline" or "candidate" ? variant : throw new ArgumentException("Invalid proposal variant."));
    internal IDisposable Enter(string id) => InvestigationFiles.Lock(Folder(id), "operation.lock");
    internal void Save(ChangeProposal proposal) => InvestigationFiles.Write(Path.Combine(Folder(proposal.Id), "proposal.json"), proposal);
    public ChangeProposal Read(string id)
    {
        var p = InvestigationFiles.Read<ChangeProposal>(Path.Combine(Folder(id), "proposal.json"));
        if (p.Id != id || p.Inputs is null || p.Companions is null || p.Inputs.Count > MaximumFiles || p.Companions.Count > 80)
            throw new InvalidDataException("Proposal manifest is inconsistent.");
        _guard.RequireDirectory(p.Root);
        _guard.RequireFile(Within(p.Root, p.File), ".cs");
        _guard.RequireProjectWithin(p.Root, p.Project);
        if (p.Inputs.Select(i => i.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != p.Inputs.Count)
            throw new InvalidDataException("Proposal has duplicate input paths.");
        return p;
    }
    internal static string Within(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new ArgumentException("Proposal paths must be relative and non-empty.");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!InvestigationFiles.Within(root, full) || full == Path.GetFullPath(root)) throw new UnauthorizedAccessException("Proposal path escapes its copy root.");
        InvestigationFiles.NoLinks(full); return full;
    }
    internal static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');
    internal static string Decode(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), true);
        return reader.ReadToEnd();
    }
    internal static byte[] EncodeLike(byte[] original, string replacement)
    {
        Encoding encoding = original.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }) || original.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })
            ? throw new ArgumentException("UTF-32 source edits are not supported.")
            : original.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }) ? new UnicodeEncoding(false, true, true)
            : original.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }) ? new UnicodeEncoding(true, true, true)
            : new UTF8Encoding(original.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }), true);
        var before = Decode(original);
        var normalized = ReviewProgress.Normalize(replacement);
        if (before.Contains("\r\n", StringComparison.Ordinal)) normalized = normalized.Replace("\n", "\r\n");
        return encoding.GetPreamble().Concat(encoding.GetBytes(normalized)).ToArray();
    }
    private IReadOnlyList<ProposalInput> Scan(string root, CancellationToken ct)
    {
        var inputs = new List<ProposalInput>(); var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0)); long total = 0; var visited = 0;
        while (pending.Count > 0)
        {
            var (directory, depth) = pending.Pop();
            if (depth > 32) throw new InvalidOperationException("Proposal copy depth exceeds 32.");
            InvestigationFiles.NoLinks(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                if (++visited > 16000) throw new InvalidOperationException("Proposal directory-entry budget exceeded.");
                var name = Path.GetFileName(path);
                if (ExcludedDirectories.Contains(name) || name.StartsWith(".tenka-change-", StringComparison.Ordinal)) continue;
                // Never recursively copy the operator's Tenka data directory into itself.
                if (!InvestigationFiles.Within(_root, root) && (InvestigationFiles.Within(_root, path) || Path.GetFullPath(path) == Path.GetDirectoryName(_root))) continue;
                InvestigationFiles.NoLinks(path);
                if (Directory.Exists(path)) { pending.Push((path, depth + 1)); continue; }
                if (inputs.Count >= MaximumFiles) throw new InvalidOperationException("Proposal file budget exceeded; open a narrower workspace.");
                var bytes = InvestigationFiles.Bytes(path, MaximumFileBytes); total += bytes.Length;
                if (total > MaximumTotalBytes) throw new InvalidOperationException("Proposal byte budget exceeded; open a narrower workspace.");
                inputs.Add(new(Relative(root, path), InvestigationFiles.Hash(bytes), bytes.Length));
            }
        }
        return inputs.OrderBy(x => x.Path, StringComparer.Ordinal).ToArray();
    }
    internal void VerifyInputs(ChangeProposal p, string root, bool patched, CancellationToken ct)
    {
        var actual = Scan(root, ct).ToDictionary(i => i.Path, StringComparer.Ordinal);
        if (actual.Count != p.Inputs.Count) throw new InvalidOperationException("proposal_inputs_changed: Files were added/removed. Prepare a new proposal; no implicit rebase.");
        foreach (var input in p.Inputs)
        {
            var expected = patched && input.Path == p.File ? p.AfterHash : input.Hash;
            if (!actual.TryGetValue(input.Path, out var present) || present.Hash != expected)
                throw new InvalidOperationException("proposal_inputs_changed: A captured source/build input changed: " + input.Path);
        }
    }
    internal static void CheckBuildPaths(string root, IReadOnlyList<ProposalInput> inputs)
    {
        var present = inputs.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs.Where(i => Path.GetExtension(i.Path).ToLowerInvariant() is ".csproj" or ".props" or ".targets"))
        {
            var file = Within(root, input.Path);
            using var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
            var xml = XDocument.Load(reader);
            foreach (var item in xml.Descendants())
            {
                var name = item.Name.LocalName;
                if (name is "BaseOutputPath" or "OutputPath" or "OutDir" or "IntermediateOutputPath" or "BaseIntermediateOutputPath" or "MSBuildProjectExtensionsPath" or "RestorePackagesPath")
                    throw new InvalidOperationException("copy_scope_unsupported: Explicit output/cache overrides need a dedicated staging adapter: " + name);
                string? reference = name switch
                {
                    "ProjectReference" or "Compile" or "Content" or "None" or "EmbeddedResource" or "AdditionalFiles" => (string?)item.Attribute("Include"),
                    "Import" => (string?)item.Attribute("Project"),
                    "HintPath" => item.Value,
                    _ => null
                };
                if (reference is null) continue;
                foreach (var part in reference.Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var path = part.Trim().Replace('\\', Path.DirectorySeparatorChar);
                    if (path.Contains("$(", StringComparison.Ordinal) || path.Contains("@(", StringComparison.Ordinal) || Path.IsPathRooted(path))
                        throw new InvalidOperationException("copy_scope_unsupported: Dynamic/absolute build input paths are not assumed safe to relocate.");
                    var prefix = path.Split('*', '?')[0];
                    var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, prefix.Length == 0 ? "." : prefix));
                    if (!InvestigationFiles.Within(root, resolved)) throw new InvalidOperationException("copy_scope_incomplete: A build input escapes the captured workspace.");
                    if ((name is "ProjectReference" or "Import" or "HintPath") && !path.Contains('*') && !path.Contains('?') && !present.Contains(Relative(root, resolved)))
                        throw new InvalidOperationException("copy_scope_incomplete: Referenced project/import/assembly was not captured.");
                }
            }
        }
    }
    public object EditInput(ReviewTarget target)
    {
        var bytes = InvestigationFiles.Bytes(_guard.RequireFile(target.File, ".cs"), 500_000);
        var text = Decode(bytes);
        return new { file = target.File, expectedFileSha256 = InvestigationFiles.Hash(bytes),
            replacementScope = "complete_existing_file", replacementSupported = text.Length <= 60000,
            source = text.Length <= 60000 ? text : null,
            note = "Read the complete file before replacing it. The hash is byte-exact; BOM and original newline style are preserved." };
    }
    public ChangeProposal Create(string investigation, string snapshot, ReviewTarget target, string workspaceRoot,
        string expectedFileHash, string replacement, IReadOnlyList<ExplorerCompanion> companions, CancellationToken ct)
    {
        if (replacement.Length is < 1 or > 60000) throw new ArgumentException("replacementCode must contain 1..60000 characters (the complete file).");
        InvestigationFiles.Id(investigation);
        var root = _guard.RequireDirectory(workspaceRoot);
        if (root == Path.GetPathRoot(root) || InvestigationFiles.Within(root, _root) && root == Path.GetDirectoryName(_root))
            throw new ArgumentException("A filesystem/data root is not a proposal workspace.");
        var file = _guard.RequireFile(target.File, ".cs");
        if (!InvestigationFiles.Within(root, file) || !InvestigationFiles.Within(root, target.ProjectPath)) throw new InvalidOperationException("Target is outside the selected workspace.");
        var before = InvestigationFiles.Bytes(file, 500_000);
        if (InvestigationFiles.Hash(before) != expectedFileHash) throw new ReviewConflictException("file_changed: Get editInput from tenka_review_workflow and re-read the file; nothing was copied or applied.");
        var after = EncodeLike(before, replacement);
        if (CSharpSyntaxTree.ParseText(Decode(after)).GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error))
            throw new ArgumentException("replacementCode contains C# syntax errors. No proposal was created.");
        if (InvestigationFiles.Hash(before) == InvestigationFiles.Hash(after)) throw new ArgumentException("Replacement is identical to the current file.");
        // Parent build imports are not silently lost when moving a subproject.
        for (var parent = Path.GetDirectoryName(root); parent is not null; parent = Path.GetDirectoryName(parent))
            foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
                if (File.Exists(Path.Combine(parent, name))) throw new InvalidOperationException("copy_scope_incomplete: Open a solution/root containing the ancestor " + name + ".");
        var inputs = Scan(root, ct);
        if (!inputs.Any(i => i.Path == Relative(root, file) && i.Hash == expectedFileHash)) throw new ReviewConflictException("File changed while capturing the proposal.");
        CheckBuildPaths(root, inputs);
        var id = Guid.NewGuid().ToString("N"); var folder = InvestigationFiles.DirectoryAt(Folder(id));
        var p = new ChangeProposal(id, investigation, target.Id, snapshot, root, Relative(root, target.ProjectPath), Relative(root, file),
            target.SourceHash, target.ContextHash, InvestigationFiles.Hash(before), InvestigationFiles.Hash(after), DateTimeOffset.UtcNow.ToString("O"), "prepared", inputs,
            companions.Where(c => InvestigationFiles.Within(root, c.ProjectPath)).Select(c => new ProposalCompanion(c.Id, Relative(root, c.ProjectPath), c.Kind)).ToArray());
        try
        {
            File.WriteAllBytes(Path.Combine(folder, "before.bin"), before); File.WriteAllBytes(Path.Combine(folder, "after.bin"), after);
            foreach (var variant in new[] { "baseline", "candidate" })
            {
                var destination = InvestigationFiles.DirectoryAt(CopyRoot(id, variant));
                foreach (var input in inputs)
                {
                    ct.ThrowIfCancellationRequested();
                    var bytes = InvestigationFiles.Bytes(Within(root, input.Path), MaximumFileBytes);
                    if (InvestigationFiles.Hash(bytes) != input.Hash) throw new ReviewConflictException("Workspace changed during copy; proposal was not published.");
                    var copy = Within(destination, input.Path); InvestigationFiles.DirectoryAt(Path.GetDirectoryName(copy)!);
                    File.WriteAllBytes(copy, variant == "candidate" && input.Path == p.File ? after : bytes);
                }
            }
            // Empty boundaries prevent importing operator data-dir props above the copied root.
            foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
                File.WriteAllText(Path.Combine(folder, name), "<Project />");
            for (var directory = root; directory is not null; directory = Path.GetDirectoryName(directory))
                if (File.Exists(Path.Combine(directory, "global.json")))
                { File.WriteAllBytes(Path.Combine(folder, "global.json"), InvestigationFiles.Bytes(_guard.RequireFile(Path.Combine(directory, "global.json"), ".json"), 16000)); break; }
            VerifyInputs(p, root, false, ct); Save(p); return p;
        }
        catch
        {
            // Do not recursively delete a directory that another host process might have changed.
            // Incomplete, unpublished copies have no proposal.json and cannot be applied.
            throw;
        }
    }
    public object Diff(string id, int offset, int limit)
    {
        var p = Read(id);
        var before = InvestigationFiles.Bytes(Path.Combine(Folder(id), "before.bin"), 500_000);
        var after = InvestigationFiles.Bytes(Path.Combine(Folder(id), "after.bin"), 500_000);
        CheckPayloads(p, before, after);
        return new { proposalId = id, p.TargetId, p.Status, p.File, p.BeforeHash, p.AfterHash,
            diff = _diff.Compare(Decode(before), Decode(after), offset, limit), sourceModified = false, p.BackupPath,
            liveFileHash = InvestigationFiles.Hash(InvestigationFiles.Bytes(_guard.RequireFile(Within(p.Root, p.File), ".cs"), 500_000)),
            note = "Candidate copies are independent files. Copying is not a security sandbox. Applying requires a separate approval and a passing validation record." };
    }
    private static void CheckPayloads(ChangeProposal p, byte[] before, byte[] after)
    {
        if (InvestigationFiles.Hash(before) != p.BeforeHash || InvestigationFiles.Hash(after) != p.AfterHash)
            throw new InvalidDataException("Proposal replacement/backup content changed.");
    }
    public object Replace(string id, bool revert, bool approved, CancellationToken ct)
    {
        if (!approved) throw new UnauthorizedAccessException("approval_required: Source replacement requires explicit approval and approved:true.");
        using var busy = Enter(id); var p = Read(id);
        if (p.Status is "applying" or "reverting") throw new InvalidOperationException("proposal_recovery_required: An earlier replacement ended without a receipt. Inspect the saved backup and current hashes before any further edit.");
        if (revert ? p.Status != "applied" : p.Status != "prepared") throw new InvalidOperationException("Proposal is not in the required state. No replacement was repeated.");
        if (!revert)
        {
            var validation = ReadValidation(id);
            if (validation.ProposalId != id || validation.AfterHash != p.AfterHash || !ProposalValidationRunner.CanApply(validation))
                throw new InvalidOperationException("validation_required: Both copies must pass the selected tests and complete comparable benchmark reports before applying.");
            ProposalGeneratedExperiments.VerifyAll(this, p, validation);
            VerifyInputs(p, CopyRoot(id, "baseline"), false, ct);
            VerifyInputs(p, CopyRoot(id, "candidate"), true, ct);
            VerifyInputs(p, p.Root, false, ct);
        }
        var before = InvestigationFiles.Bytes(Path.Combine(Folder(id), "before.bin"), 500_000);
        var after = InvestigationFiles.Bytes(Path.Combine(Folder(id), "after.bin"), 500_000);
        CheckPayloads(p, before, after);
        var original = _guard.RequireFile(Within(p.Root, p.File), ".cs");
        var expected = revert ? p.AfterHash : p.BeforeHash;
        if (InvestigationFiles.Hash(InvestigationFiles.Bytes(original, 500_000)) != expected)
            throw new ReviewConflictException("file_changed: Live source differs from the expected bytes. No forced overwrite or implicit rebase.");
        var suffix = ".tenka-change-" + Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(Path.GetDirectoryName(original)!, suffix + ".tmp");
        var backup = Path.Combine(Path.GetDirectoryName(original)!, suffix + ".bak");
        ct.ThrowIfCancellationRequested();
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(revert ? before : after); output.Flush(true); }
            if (InvestigationFiles.Hash(InvestigationFiles.Bytes(original, 500_000)) != expected)
                throw new ReviewConflictException("file_changed: Live source changed before replacement.");
            // Same-directory temp + replace + retained displaced-file backup. There is no
            // portable compare-and-swap against a non-cooperating editor's rename; detect
            // that race using the backup and never discard or blindly overwrite it.
            Save(p with { Status = revert ? "reverting" : "applying", BackupPath = backup });
            InvestigationFiles.NoLinks(original); File.Replace(temporary, original, backup);
            if (InvestigationFiles.Hash(InvestigationFiles.Bytes(backup, 500_000)) != expected)
            {
                Save(p with { Status = "conflicted", BackupPath = backup });
                throw new ReviewConflictException("concurrent_write_detected: The displaced file differed. It is retained at the reported backup path; no automatic rollback was attempted.");
            }
            Save(p with { Status = revert ? "reverted" : "applied", BackupPath = backup });
            return new { proposalId = id, status = revert ? "reverted" : "applied", backupPath = backup, requiresReload = true,
                note = "Only this file was replaced. Reload before further review. The backup is retained; this operation does not assert a measured speedup or mark the code optimized." };
        }
        catch
        {
            // An intent remains recoverable if source changed but recording completion failed.
            // No automatic repeat, forced revert or cleanup of displaced content.
            throw;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal void SaveValidation(ProposalValidation report) => InvestigationFiles.Write(Path.Combine(Folder(report.ProposalId), "validation.json"), report);
    public ProposalValidation ReadValidation(string id) => InvestigationFiles.Read<ProposalValidation>(Path.Combine(Folder(id), "validation.json"));
}