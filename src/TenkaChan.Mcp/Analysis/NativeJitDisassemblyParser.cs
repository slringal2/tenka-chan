using System.Text.RegularExpressions;

namespace TenkaChan.Mcp.Analysis;

public sealed record NativeInstruction(int? NativeOffset, string EncodedBytes,
    string Mnemonic, string Operands, string Family);

public sealed record NativePerformanceSignal(string Kind, string Evidence,
    string Interpretation, int InstructionIndex);

public sealed record NativeMethodDisassembly(
    string JitSignature, string DeclaringType, string MethodName, string JitMode,
    string Architecture, int? CodeSizeBytes,
    IReadOnlyList<NativeInstruction> Instructions,
    IReadOnlyList<NativePerformanceSignal> Signals,
    IReadOnlyDictionary<string, int> InstructionFamilies,
    string? RoslynMethodId = null, string MatchStatus = "not_matched")
{
    public string? RoslynSourcePath { get; init; }
    public int? RoslynSourceLine { get; init; }
    public string? RoslynSignature { get; init; }
}

public sealed record NativeCodeReport(
    string TargetFramework, string RuntimeImage, string JitFilter,
    string SourceFile, int MethodCount, int InstructionCount,
    bool Truncated, IReadOnlyList<NativeMethodDisassembly> Methods,
    IReadOnlyList<string> Limitations)
{
    public string? RoslynSnapshotId { get; init; }
}

/// <summary>
/// Parses textual RyuJIT generated assembly (not managed IL) emitted by
/// DOTNET_JitDisasm + DOTNET_JitDisasmWithCodeBytes in a sandbox.
/// Only mechanical facts and possible performance-relevant signals are
/// returned; register use or a helper call is not itself proof of slowness.
/// </summary>
public static class NativeJitDisassemblyParser
{
    private static readonly Regex Header = new(
        @"^\s*;\s*Assembly listing for method (?<signature>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex InstructionWithBytes = new(
        @"^\s*(?<bytes>(?:[0-9a-fA-F]{2}){1,24})\s+(?<mnemonic>[A-Za-z][A-Za-z0-9_.]*)\s*(?<operands>.*?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex InstructionWithoutBytes = new(
        @"^\s{2,}(?<mnemonic>[A-Za-z][A-Za-z0-9_.]*)\s+(?<operands>.*?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CodeBytes = new(
        @"^\s*;\s*Total bytes of code (?<count>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static NativeCodeReport Read(string path, string framework,
        string runtimeImage, string filter, int maxMethods = 20, int maxInstructions = 6000)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Native JIT report missing.", path);
        if (new FileInfo(path).Length > 8_500_000)
            throw new InvalidDataException("Native JIT output exceeds the size limit.");
        return Parse(File.ReadAllText(path), framework, runtimeImage, filter,
            path, maxMethods, maxInstructions);
    }

    public static NativeCodeReport Parse(string text, string framework,
        string runtimeImage, string filter, string sourceFile = "(memory)",
        int maxMethods = 20, int maxInstructions = 6000)
    {
        if (text.Length > 8_500_000)
            throw new InvalidDataException("Native JIT output exceeds the size limit.");
        maxMethods = Math.Clamp(maxMethods, 1, 50);
        maxInstructions = Math.Clamp(maxInstructions, 1, 20000);
        var methods = new List<NativeMethodDisassembly>();
        var instructions = new List<NativeInstruction>();
        var signals = new List<NativePerformanceSignal>();
        var signature = "";
        var mode = "";
        var architecture = "unknown";
        int? nativeByteCount = null;
        int? offset = 0;
        var truncated = false;
        var totalInstructions = 0;

        void Flush()
        {
            if (string.IsNullOrEmpty(signature)) return;
            if (methods.Count >= maxMethods) { truncated = true; return; }
            var (declaringType, methodName) = ParseMemberIdentity(signature);
            var families = instructions
                .GroupBy(i => i.Family, StringComparer.Ordinal)
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
            methods.Add(new NativeMethodDisassembly(signature, declaringType,
                methodName, mode, architecture, nativeByteCount,
                instructions.ToArray(), signals.ToArray(), families));
        }

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var header = Header.Match(line);
            if (header.Success)
            {
                Flush();
                signature = header.Groups["signature"].Value;
                var optsIndex = signature.LastIndexOf(" (", StringComparison.Ordinal);
                mode = "";
                if (optsIndex >= 0 && signature.EndsWith(')'))
                {
                    mode = signature[(optsIndex + 2)..^1];
                    signature = signature[..optsIndex];
                }
                instructions.Clear();
                signals.Clear();
                nativeByteCount = null;
                offset = 0;
                architecture = "unknown";
                continue;
            }
            if (string.IsNullOrEmpty(signature)) continue;
            if (line.StartsWith("; Emitting ", StringComparison.Ordinal))
            {
                if (line.Contains("X64", StringComparison.OrdinalIgnoreCase))
                    architecture = "x64";
                else if (line.Contains("ARM64", StringComparison.OrdinalIgnoreCase))
                    architecture = "arm64";
            }
            var codeBytes = CodeBytes.Match(line);
            if (codeBytes.Success &&
                int.TryParse(codeBytes.Groups["count"].Value, out var byteCount))
            {
                nativeByteCount = byteCount;
                continue;
            }
            if (methods.Count >= maxMethods || totalInstructions >= maxInstructions)
            {
                truncated = true;
                continue;
            }

            // Remove RyuJIT's trailing inline comments before matching.
            var semicolon = line.IndexOf(";;", StringComparison.Ordinal);
            var cleaned = semicolon >= 0 ? line[..semicolon] : line;
            var match = InstructionWithBytes.Match(cleaned);
            var bytes = match.Success ? match.Groups["bytes"].Value : "";
            if (!match.Success)
            {
                // When byte output is disabled, a narrower, intentionally
                // conservative fallback accepts only indented asm with operands.
                if (cleaned.TrimStart().StartsWith(';') || cleaned.Contains(':'))
                    continue;
                match = InstructionWithoutBytes.Match(cleaned);
            }
            if (!match.Success) continue;
            var mnemonic = match.Groups["mnemonic"].Value.ToLowerInvariant();
            if (!IsInstructionMnemonic(mnemonic)) continue;
            var operands = match.Groups["operands"].Value.Trim();
            var family = Classify(mnemonic, operands);
            var index = instructions.Count;
            instructions.Add(new NativeInstruction(offset, bytes.ToUpperInvariant(),
                mnemonic, operands, family));
            if (bytes.Length > 0) offset += bytes.Length / 2;
            else offset = null;
            totalInstructions++;

            var helper = DetectPerformanceSignal(mnemonic, operands, family, index);
            if (helper is not null) signals.Add(helper);
        }
        Flush();
        return new NativeCodeReport(framework, runtimeImage, filter,
            sourceFile, methods.Count, methods.Sum(x => x.Instructions.Count),
            truncated, methods,
            [
                "Machine instructions are the JIT's code for the observed runtime, architecture and compilation mode, not the original C# or managed IL.",
                "Mapping back to source is method-level only when unambiguous. Exact machine-instruction to C# line/IL offset mapping requires JIT/PDB sequence information and is not inferred.",
                "Native instruction patterns are facts about code generation but NOT measured CPU costs, allocations, SIMD speedups or proof of a regression.",
                "Tiered compilation, ReadyToRun, dynamic PGO and input workload can change native instructions. The controlled harness disables tiering and ReadyToRun for reproducibility.",
                "Only methods JIT-compiled and matching the requested selector appear. An absent method is not evidence that it never runs."
            ]);
    }

    private static (string Type, string Method) ParseMemberIdentity(string value)
    {
        var colon = value.IndexOf(':');
        if (colon < 0) return ("", value.Split('(')[0]);
        var type = value[..colon].Trim();
        var method = value[(colon + 1)..];
        var paren = method.IndexOf('(');
        if (paren >= 0) method = method[..paren];
        var generic = method.IndexOf('[');
        if (generic >= 0) method = method[..generic];
        return (type, method.Trim());
    }

    private static bool IsInstructionMnemonic(string mnemonic)
    {
        if (mnemonic is "nop" or "ret" or "int3" or "ud2" or "call" or "jmp"
            or "push" or "pop" or "cmp" or "test" or "lea" or "mov" or "movzx"
            or "movsx" or "movsxd" or "add" or "sub" or "imul" or "mul"
            or "idiv" or "div" or "xor" or "or" or "and" or "inc" or "dec"
            or "shl" or "shr" or "sar" or "sal" or "bt" or "bts" or "btc"
            or "lock" or "cmpxchg" or "xchg" or "pause" or "vzeroupper"
            or "cdq" or "cqo" or "movaps" or "movups" or "movdqa"
            or "vmovdqa" or "vmovdqu" or "ldr" or "str" or "ldp" or "stp"
            or "bl" or "blr" or "br" or "b" or "cbz" or "cbnz" or "tbz"
            or "tbnz" or "adrp" or "fadd" or "fmul" or "fdiv" or "sdiv"
            or "udiv")
            return true;
        return (mnemonic.StartsWith('j') && mnemonic.Length <= 7) ||
            mnemonic.StartsWith('v') ||
            mnemonic.StartsWith("cmov", StringComparison.Ordinal) ||
            mnemonic.StartsWith("set", StringComparison.Ordinal) ||
            mnemonic.StartsWith("padd", StringComparison.Ordinal) ||
            mnemonic.StartsWith("mov", StringComparison.Ordinal) ||
            mnemonic.StartsWith("add", StringComparison.Ordinal) ||
            mnemonic.StartsWith("sub", StringComparison.Ordinal) ||
            mnemonic.StartsWith("mul", StringComparison.Ordinal) ||
            mnemonic.StartsWith("f", StringComparison.Ordinal);
    }

    private static string Classify(string mnemonic, string operands)
    {
        if (mnemonic is "call" or "bl" or "blr") return "call";
        if (mnemonic is "idiv" or "div" or "sdiv" or "udiv" or "fdiv") return "division";
        if (mnemonic == "jmp" || mnemonic == "b") return "unconditional_branch";
        if (mnemonic is "cbz" or "cbnz" or "tbz" or "tbnz" ||
            mnemonic.StartsWith('j') && mnemonic != "jmp")
            return "conditional_branch";
        if (mnemonic.StartsWith('v') || mnemonic.Contains("ps", StringComparison.Ordinal) ||
            mnemonic.Contains("pd", StringComparison.Ordinal) ||
            mnemonic.StartsWith("padd", StringComparison.Ordinal))
            return "vector_candidate";
        if (mnemonic is "lock" or "cmpxchg" or "xchg") return "synchronization";
        if (mnemonic is "cmp" or "test") return "comparison";
        if (mnemonic is "ret") return "return";
        if (mnemonic is "ldr" or "str" or "ldp" or "stp" ||
            mnemonic.StartsWith("mov", StringComparison.Ordinal) ||
            mnemonic is "lea" or "push" or "pop") return "data_movement";
        return "compute_or_other";
    }

    private static NativePerformanceSignal? DetectPerformanceSignal(
        string mnemonic, string operands, string family, int index)
    {
        if (operands.Contains("CORINFO_HELP_NEWSFAST", StringComparison.OrdinalIgnoreCase) ||
            operands.Contains("RhpNewFast", StringComparison.OrdinalIgnoreCase) ||
            operands.Contains("CORINFO_HELP_NEWARR", StringComparison.OrdinalIgnoreCase))
            return new NativePerformanceSignal("allocation_helper", operands,
                "A runtime allocation helper call appears in native code; count allocations under workload.", index);
        if (operands.Contains("CORINFO_HELP_RNGCHKFAIL", StringComparison.OrdinalIgnoreCase) ||
            operands.Contains("ThrowIndexOutOfRangeException", StringComparison.OrdinalIgnoreCase))
            return new NativePerformanceSignal("bounds_check_failure_path", operands,
                "Native code retains a bounds-check failure path; no claim of frequent failures.", index);
        if (family == "division")
            return new NativePerformanceSignal("division", mnemonic + " " + operands,
                "Division instruction emitted. Actual latency depends on architecture and operands.", index);
        if (family == "vector_candidate")
            return new NativePerformanceSignal("vector_instruction", mnemonic + " " + operands,
                "Potential vector instruction; register width and semantics require inspection.", index);
        if (family == "call")
            return new NativePerformanceSignal("native_call", mnemonic + " " + operands,
                "Call instruction emitted; runtime frequency and inlining performance are unknown.", index);
        return null;
    }
}
