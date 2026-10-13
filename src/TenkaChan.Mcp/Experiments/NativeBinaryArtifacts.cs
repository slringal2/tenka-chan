using System.Text;
using System.Text.RegularExpressions;
using TenkaChan.Mcp.Analysis;
using TenkaChan.Mcp.Security;

namespace TenkaChan.Mcp.Experiments;

public sealed record NativeBinaryArtifact(string FileName, long ByteLength, string Sha256);

internal static class NativeBinaryArtifacts
{
    public static string CaptureScript(string tfm)
    {
        _ = TargetFrameworkSelection.FromValidated(tfm, "jit_artifact_capture");
        return "artifact_total=0\nartifact_count=0\n" +
            "for binary in /work/generated/bin/Release/" + tfm + "/*.dll /work/generated/bin/Release/" + tfm + "/*.pdb; do\n" +
            "  [ -f \"$binary\" ] && [ ! -L \"$binary\" ] || continue\n" +
            "  base=${binary##*/}\n" +
            "  case \"$base\" in *[!A-Za-z0-9_.-]*) continue ;; esac\n" +
            "  size=$(wc -c < \"$binary\")\n" +
            "  if [ \"$size\" -gt 32000000 ] || [ $((artifact_total + size)) -gt 96000000 ] || [ \"$artifact_count\" -ge 60 ]; then\n" +
            "    touch /artifacts/binary-capture-truncated.marker\n    continue\n  fi\n" +
            "  cp -- \"$binary\" \"/artifacts/binary-$base\"\n" +
            "  artifact_total=$((artifact_total + size))\n  artifact_count=$((artifact_count + 1))\n" +
            "done\ndotnet --info > /artifacts/runtime-info.txt\n";
    }

    public static IReadOnlyList<NativeBinaryArtifact> Snapshot(string directory)
    {
        var guard = new WorkspaceGuard([directory], false, false);
        guard.RequireDirectory(directory);
        var records = new List<NativeBinaryArtifact>();
        long total = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "binary-*", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (!ValidName(name)) continue;
            if (records.Count >= 60) throw new InvalidDataException("Too many captured binaries.");
            var safe = guard.RequireFile(path, Path.GetExtension(path));
            var bytes = BoundedBinaryFile.Read(safe, 32_000_000);
            total += bytes.Length;
            if (total > 96_000_000) throw new InvalidDataException("Captured binary budget exceeded.");
            records.Add(new NativeBinaryArtifact(name, bytes.Length, BinarySourceInspector.Hash(bytes)));
        }
        return records;
    }

    public static string Verify(string directory, IReadOnlyList<NativeBinaryArtifact> records, string name)
    {
        if (!ValidName(name)) throw new ArgumentException("Use an exact fileName from jit_binary_files; paths are not accepted.");
        var record = records.SingleOrDefault(r => r.FileName == name)
            ?? throw new KeyNotFoundException("Binary is not in this execution's captured manifest.");
        var guard = new WorkspaceGuard([directory], false, false);
        var path = guard.RequireFile(Path.Combine(directory, name), Path.GetExtension(name));
        var bytes = BoundedBinaryFile.Read(path, 32_000_000);
        if (bytes.LongLength != record.ByteLength || BinarySourceInspector.Hash(bytes) != record.Sha256)
            throw new InvalidDataException("Captured build artifact changed after execution; refusing stale evidence.");
        return path;
    }

    public static bool ValidName(string name) => Regex.IsMatch(name,
        @"^binary-[A-Za-z0-9_][A-Za-z0-9_.-]{0,220}\.(dll|pdb)$", RegexOptions.CultureInvariant);
}

/// <summary>
/// A durable single-use marker prevents concurrent reruns or stale artifacts
/// being attributed to a new execution. Retry by preparing a new experiment.
/// </summary>
internal static class ExperimentExecutionReservation
{
    public static FileStream Acquire(string directory, string artifacts)
    {
        if (File.Exists(Path.Combine(directory, "execution-report.json")) ||
            Directory.EnumerateFileSystemEntries(artifacts).Any())
            throw new InvalidOperationException("Experiment already has execution evidence. Prepare a new experiment for a fresh run.");
        try
        {
            return new FileStream(Path.Combine(directory, "execution-started.lock"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("Experiment is running or was already attempted. Prepare a new experiment to retry.", ex);
        }
    }
}
