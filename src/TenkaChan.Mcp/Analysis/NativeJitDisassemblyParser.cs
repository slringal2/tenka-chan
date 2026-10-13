using System.Text;
using System.Text.RegularExpressions;

namespace TenkaChan.Mcp.Analysis;

public sealed record NativeInstruction(int? NativeOffset, string EncodedBytes,
    string Mnemonic, string Operands, string Family);
public sealed record NativePerformanceSignal(string Kind, string Evidence,
    string Interpretation, int InstructionIndex);
public sealed record NativeMethodDisassembly(string JitSignature, string DeclaringType,
    string MethodName, string JitMode, string Architecture, int? CodeSizeBytes,
    IReadOnlyList<NativeInstruction> Instructions, IReadOnlyList<NativePerformanceSignal> Signals,
    IReadOnlyDictionary<string, int> InstructionFamilies,
    string? RoslynMethodId = null, string MatchStatus = "not_matched")
{
    public string? RoslynSourcePath { get; init; }
    public int? RoslynSourceLine { get; init; }
    public string? RoslynSignature { get; init; }
    public bool OffsetsReliable { get; init; }
    public string OffsetValidation { get; init; } = "unverified_legacy_report";
    public IReadOnlyList<string> ParseWarnings { get; init; } = [];
}
public sealed record NativeCodeReport(string TargetFramework, string RuntimeImage,
    string JitFilter, string SourceFile, int MethodCount, int InstructionCount, bool Truncated,
    IReadOnlyList<NativeMethodDisassembly> Methods, IReadOnlyList<string> Limitations)
{
    public string? RoslynSnapshotId { get; init; }
}

/// <summary>
/// Parses RyuJIT's disassembly listing; this is not a stand-alone ISA decoder.
/// Unknown instructions are retained. Offsets are withheld unless every
/// instruction has bytes and the byte total equals the JIT code-size footer.
/// </summary>
public static class NativeJitDisassemblyParser
{
    private static readonly Regex Header = new(@"^\s*;\s*Assembly listing for method (?<sig>.+?)\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex WithBytes = new(@"^\s+(?<bytes>(?:[0-9A-Fa-f]{2}){1,24})\s+(?<op>[A-Za-z][A-Za-z0-9_.]*)\s*(?<args>.*?)\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex WithoutBytes = new(@"^\s{2,}(?<op>[A-Za-z][A-Za-z0-9_.]*)(?:\s+(?<args>.*?))?\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex Footer = new(@"^\s*;\s*Total bytes of code (?<n>\d+)", RegexOptions.CultureInvariant);

    public static NativeCodeReport Read(string path, string framework, string runtimeImage,
        string filter, int maxMethods = 20, int maxInstructions = 6000) =>
        Parse(Encoding.UTF8.GetString(BoundedBinaryFile.Read(path, 8_500_000)), framework,
            runtimeImage, filter, path, maxMethods, maxInstructions);

    public static NativeCodeReport Parse(string text, string framework, string runtimeImage,
        string filter, string sourceFile = "(memory)", int maxMethods = 20, int maxInstructions = 6000)
    {
        if (text.Length > 8_500_000) throw new InvalidDataException("Native JIT listing exceeds budget.");
        maxMethods = Math.Clamp(maxMethods, 1, 50);
        maxInstructions = Math.Clamp(maxInstructions, 1, 20000);
        var methods = new List<NativeMethodDisassembly>();
        var instructions = new List<NativeInstruction>();
        var signals = new List<NativePerformanceSignal>();
        var signature = "";
        var mode = "";
        var architecture = "unknown";
        int? size = null;
        var offset = 0;
        var hasMissingBytes = false;
        var methodTruncated = false;
        var truncated = false;
        var totalInstructions = 0;
        void Flush()
        {
            if (signature.Length == 0) return;
            if (methods.Count >= maxMethods) { truncated = true; return; }
            var reliable = !hasMissingBytes && !methodTruncated && size is not null && size == offset;
            var validation = methodTruncated ? "instruction_limit_reached"
                : hasMissingBytes ? "missing_encoding_bytes"
                : size is null ? "missing_code_size_footer"
                : size != offset ? "encoded_byte_count_mismatch" : "encoded_length_matches_code_size";
            var colon = signature.IndexOf(':');
            var type = colon < 0 ? "" : signature[..colon].Trim();
            var name = (colon < 0 ? signature : signature[(colon + 1)..]).Split('(')[0];
            if (name.Contains('[')) name = name[..name.IndexOf('[')];
            var families = instructions.GroupBy(i => i.Family, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count());
            var warnings = new List<string>();
            if (!reliable) warnings.Add("Native offsets withheld: " + validation + ". No downstream source mapping should infer them.");
            if (instructions.Any(i => i.Family == "unknown"))
                warnings.Add("Unclassified instructions are preserved; unknown does not mean no CPU cost.");
            methods.Add(new NativeMethodDisassembly(signature, type, name.Trim(), mode, architecture, size,
                instructions.Select(i => reliable ? i : i with { NativeOffset = null }).ToArray(),
                signals.ToArray(), families)
            { OffsetsReliable = reliable, OffsetValidation = validation, ParseWarnings = warnings });
        }
        foreach (var raw in text.Split('\n'))
        {
            if (raw.Length > 32000) throw new InvalidDataException("Native listing line exceeds budget.");
            var line = raw.TrimEnd('\r');
            var head = Header.Match(line);
            if (head.Success)
            {
                Flush();
                signature = head.Groups["sig"].Value;
                mode = "";
                var index = signature.LastIndexOf(" (", StringComparison.Ordinal);
                if (index >= 0 && signature.EndsWith(')'))
                { mode = signature[(index + 2)..^1]; signature = signature[..index]; }
                instructions.Clear(); signals.Clear(); size = null; offset = 0;
                architecture = "unknown"; hasMissingBytes = false; methodTruncated = false;
                continue;
            }
            if (signature.Length == 0) continue;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("; Emitting ", StringComparison.Ordinal))
                architecture = line.Contains("ARM64", StringComparison.OrdinalIgnoreCase) ? "arm64"
                    : line.Contains("X64", StringComparison.OrdinalIgnoreCase) ? "x64" : "unknown";
            var footer = Footer.Match(line);
            if (footer.Success)
            { if (int.TryParse(footer.Groups["n"].Value, out var n)) size = n; continue; }
            if (trimmed.StartsWith(';') || trimmed.Length == 0 || trimmed.EndsWith(':')) continue;
            var comment = line.IndexOf(";;", StringComparison.Ordinal);
            if (comment >= 0) line = line[..comment];
            var match = WithBytes.Match(line);
            var bytes = match.Success ? match.Groups["bytes"].Value : "";
            if (!match.Success) match = WithoutBytes.Match(line);
            if (!match.Success) continue;
            if (totalInstructions >= maxInstructions || methods.Count >= maxMethods)
            { truncated = true; methodTruncated = true; continue; }
            var op = match.Groups["op"].Value.ToLowerInvariant();
            var args = match.Groups["args"].Value.Trim();
            var family = Classify(op);
            instructions.Add(new NativeInstruction(offset, bytes.ToUpperInvariant(), op, args, family));
            offset += bytes.Length / 2;
            hasMissingBytes |= bytes.Length == 0;
            totalInstructions++;
            var signal = Signal(op, args, family, instructions.Count - 1);
            if (signal is not null) signals.Add(signal);
        }
        Flush();
        return new NativeCodeReport(framework, runtimeImage, filter, sourceFile, methods.Count,
            methods.Sum(m => m.Instructions.Count), truncated, methods,
            [
                "JIT-emitted bytes and mnemonics are observed code-generation evidence, not measured instruction costs.",
                "Byte offsets require complete encodings and a matching code-size footer. Unknown instructions are retained; unverifiable offsets are null.",
                "Roslyn correlation is method-level only. Exact native-to-IL/C# correspondence additionally requires a native/IL map from this precise JIT code version.",
                "Scalar floating-point AVX instructions and vector-state operations are distinguished from potential packed-vector operations; none proves SIMD speedup.",
                "Controlled experiments disable tiering and ReadyToRun. Production code under Tiered PGO or another ISA/runtime can differ."
            ]);
    }

    private static string Classify(string op)
    {
        if (op is "call" or "bl" or "blr") return "call";
        if (op is "idiv" or "div" or "sdiv" or "udiv" or "fdiv") return "division";
        if (op is "jmp" or "b" or "br") return "unconditional_branch";
        if (op.StartsWith('j') || op.StartsWith("b.", StringComparison.Ordinal) || op is "cbz" or "cbnz" or "tbz" or "tbnz") return "conditional_branch";
        if (op is "vzeroupper" or "vzeroall") return "vector_state";
        if ((op.StartsWith('v') || op.StartsWith("add") || op.StartsWith("sub") || op.StartsWith("mul") || op.StartsWith("div") || op.StartsWith("sqrt")) &&
            (op.EndsWith("ss", StringComparison.Ordinal) || op.EndsWith("sd", StringComparison.Ordinal))) return "scalar_floating";
        if (op.StartsWith('v') || op.StartsWith("padd", StringComparison.Ordinal) || op.EndsWith("ps", StringComparison.Ordinal) || op.EndsWith("pd", StringComparison.Ordinal)) return "vector_candidate";
        if (op is "lock" or "cmpxchg" or "xchg") return "synchronization";
        if (op is "cmp" or "test") return "comparison";
        if (op == "ret") return "return";
        if (op.StartsWith("mov", StringComparison.Ordinal) || op is "lea" or "push" or "pop" or "ldr" or "str" or "ldp" or "stp") return "data_movement";
        if (op is "add" or "sub" or "imul" or "mul" or "xor" or "or" or "and" or "inc" or "dec" or "shl" or "shr" or "sar" or "sal" or "nop" or "cqo" or "cdq" or "int3" or "ud2" or "pause") return "compute_or_other";
        return "unknown";
    }
    private static NativePerformanceSignal? Signal(string op, string args, string family, int index)
    {
        if (family is "call" or "unconditional_branch")
        {
            if (args.Contains("CORINFO_HELP_NEWSFAST", StringComparison.OrdinalIgnoreCase) || args.Contains("RhpNewFast", StringComparison.OrdinalIgnoreCase) || args.Contains("CORINFO_HELP_NEWARR", StringComparison.OrdinalIgnoreCase))
                return new NativePerformanceSignal("allocation_helper", args, "Allocation helper present; measure actual allocation frequency.", index);
            if (args.Contains("CORINFO_HELP_RNGCHKFAIL", StringComparison.OrdinalIgnoreCase) || args.Contains("ThrowIndexOutOfRangeException", StringComparison.OrdinalIgnoreCase))
                return new NativePerformanceSignal("bounds_check_failure_path", args, "Failure path present; this does not count bounds checks or observed failures.", index);
        }
        return family switch
        {
            "division" => new("division", op + " " + args, "Division emitted, not a latency measurement.", index),
            "vector_candidate" => new("vector_instruction", op + " " + args, "Potential packed-vector operation; verify lane semantics and workload.", index),
            "call" => new("native_call", op + " " + args, "Native call emitted; cost and execution frequency are unknown.", index),
            _ => null
        };
    }
}
