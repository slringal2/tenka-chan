using System.Security.Cryptography;
using Iced.Intel;

namespace TenkaChan.Mcp.Analysis;

public sealed record DecodedRegister(string Register, string Access);
public sealed record DecodedMemory(string Base, string Index, int Scale, string Access, string Size);
public sealed record DecodedNativeInstruction(int Offset, int Length, string Bytes,
    string Assembly, string Mnemonic, string FlowControl, string[] CpuFeatures,
    IReadOnlyList<DecodedRegister> Registers, IReadOnlyList<DecodedMemory> Memory,
    int? LocalBranchTarget, string BranchTargetStatus, string ListingMnemonic,
    string ListingAgreement);
public sealed record NativeFlowEdge(int FromBlock, int? ToBlock, string Kind, int? TargetOffset);
public sealed record NativeBasicBlock(int Id, int StartOffset, int EndOffsetExclusive,
    int InstructionCount);
public sealed record AssemblyInvestigationSite(int Offset, string Kind, string Priority,
    string EvidenceLevel, string Reason, bool DefectConfirmed = false);
public sealed record NativeInstructionAnalysis(string Signature, string Architecture,
    string Status, bool BytesDecoded, string? CodeSha256, int CodeSizeBytes,
    string ControlFlowStatus, IReadOnlyList<DecodedNativeInstruction> Instructions,
    IReadOnlyList<NativeBasicBlock> Blocks, IReadOnlyList<NativeFlowEdge> Edges,
    IReadOnlyList<AssemblyInvestigationSite> InvestigationSites,
    IReadOnlyDictionary<string, int> FlowCounts, IReadOnlyList<string> CpuFeatures,
    IReadOnlyList<string> Limitations);

/// <summary>
/// Decode actual x86/x64 bytes, rather than trusting listing mnemonics. All
/// offsets are relative to the selected method. No code is executed and no
/// native-to-IL/source, instruction latency, hotness or speedup is inferred.
/// </summary>
public static class NativeInstructionAnalyzer
{
    public const string Version = "2026.10-native.1";
    public const int MaximumInstructions = 6000;

    public static NativeInstructionAnalysis Analyze(NativeMethodDisassembly method)
    {
        var limitations = new List<string>
        {
            "Decoded bytes establish instruction semantics, not execution frequency, cycles, cache misses or a performance regression.",
            "Offsets and branch targets are method-relative. External/indirect targets are unresolved; no process addresses or native-to-IL/source mapping are invented.",
            "The graph describes normal branch flow only. Exception edges and interprocedural/non-returning call behavior are not modeled.",
            "A backwards branch is a loop candidate, not proof that a loop executes. Stack-based memory operands are not automatically register spills.",
            "CPUID features describe the encoding. They are not a check of the target CPU/OS feature support. Scalar AVX does not prove packed SIMD.",
            "Input listings and saved reports are not authenticated. Iced verifies decoding, not capture/build/source identity."
        };
        NativeInstructionAnalysis Refuse(string status) => new(method.JitSignature,
            method.Architecture, status, false, null, method.CodeSizeBytes ?? 0,
            "unavailable", [], [], [], [], new Dictionary<string, int>(), [], limitations);
        var bitness = method.Architecture.ToLowerInvariant() switch
        { "x64" => 64, "x86" => 32, _ => 0 };
        if (bitness == 0) return Refuse("unsupported_architecture");
        if (!method.OffsetsReliable || method.CodeSizeBytes is null || method.Instructions.Count == 0)
            return Refuse("incomplete_or_unverified_encodings");
        if (method.Instructions.Count > MaximumInstructions || method.CodeSizeBytes > 100_000)
            return Refuse("analysis_budget_exceeded");

        var bytes = new List<byte>();
        foreach (var row in method.Instructions)
        {
            if (row.NativeOffset != bytes.Count || row.EncodedBytes.Length is < 2 or > 30 ||
                row.EncodedBytes.Length % 2 != 0 || !row.EncodedBytes.All(Uri.IsHexDigit))
                return Refuse("invalid_encoding_or_noncontiguous_offsets");
            bytes.AddRange(Convert.FromHexString(row.EncodedBytes));
        }
        if (bytes.Count != method.CodeSizeBytes) return Refuse("encoded_size_mismatch");
        var codeBytes = bytes.ToArray();
        var decoder = Decoder.Create(bitness, new ByteArrayCodeReader(codeBytes));
        decoder.IP = 0;
        var raw = new List<Instruction>();
        foreach (var row in method.Instructions)
        {
            var instruction = decoder.Decode();
            if (instruction.IsInvalid || instruction.IP != (ulong)row.NativeOffset!.Value ||
                instruction.Length != row.EncodedBytes.Length / 2)
                return Refuse("decoder_rejected_listing_boundaries");
            raw.Add(instruction);
        }
        if (decoder.IP != (ulong)codeBytes.Length) return Refuse("decoder_size_mismatch");

        var offsets = raw.Select(i => (int)i.IP).ToHashSet();
        var decoded = new List<DecodedNativeInstruction>();
        var sites = new List<AssemblyInvestigationSite>();
        var infoFactory = new InstructionInfoFactory();
        var formatter = new IntelFormatter();
        var output = new StringOutput();
        for (var n = 0; n < raw.Count; n++)
        {
            var i = raw[n];
            var offset = (int)i.IP;
            var info = infoFactory.GetInfo(i);
            var registers = new List<DecodedRegister>();
            foreach (var register in info.GetUsedRegisters())
                registers.Add(new(register.Register.ToString(), register.Access.ToString()));
            var memory = new List<DecodedMemory>();
            foreach (var operand in info.GetUsedMemory())
                memory.Add(new(operand.Base.ToString(), operand.Index.ToString(),
                    operand.Scale, operand.Access.ToString(), operand.MemorySize.ToString()));
            var near = i.Op0Kind is OpKind.NearBranch16 or OpKind.NearBranch32 or OpKind.NearBranch64;
            var branchLike = i.FlowControl is FlowControl.ConditionalBranch or
                FlowControl.UnconditionalBranch or FlowControl.Call or FlowControl.XbeginXabortXend;
            int? target = near && branchLike && i.NearBranchTarget < (ulong)codeBytes.Length
                ? (int)i.NearBranchTarget : null;
            var targetStatus = near && branchLike
                ? target.HasValue ? offsets.Contains(target.Value) ? "local_instruction_boundary"
                    : "inside_instruction_unresolved" : "outside_method_unresolved"
                : i.FlowControl is FlowControl.IndirectBranch or FlowControl.IndirectCall
                    ? "indirect_unresolved" : "not_a_direct_branch";
            formatter.Format(i, output);
            var mnemonic = i.Mnemonic.ToString().ToLowerInvariant();
            var listingMnemonic = method.Instructions[n].Mnemonic;
            decoded.Add(new(offset, i.Length, Convert.ToHexString(codeBytes.AsSpan(offset, i.Length)),
                output.ToStringAndReset(), mnemonic, i.FlowControl.ToString(),
                i.CpuidFeatures.Select(x => x.ToString()).OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                registers, memory, target, targetStatus, listingMnemonic,
                string.Equals(listingMnemonic, mnemonic, StringComparison.OrdinalIgnoreCase)
                    ? "same_mnemonic" : "different_mnemonic_or_alias__bytes_take_precedence"));
            void Site(string kind, string reason) => sites.Add(new(offset, kind,
                "P2", "decoded_instruction", reason));
            if (i.Mnemonic is Mnemonic.Div or Mnemonic.Idiv)
                Site("integer_division", "Integer division is emitted. Measure input frequency before proposing a replacement.");
            if (i.HasLockPrefix || i.Mnemonic is Mnemonic.Mfence or Mnemonic.Lfence or Mnemonic.Sfence)
                Site("synchronization_instruction", "Atomic/fence instruction is present; contention or waiting is not established.");
            if (i.FlowControl == FlowControl.IndirectCall)
                Site("indirect_call", "Indirect call is emitted; devirtualization potential and actual target cost are unknown.");
            if (targetStatus == "local_instruction_boundary" && target <= offset &&
                i.FlowControl is FlowControl.ConditionalBranch or FlowControl.UnconditionalBranch)
                Site("backward_branch_candidate", "Backward control-flow edge; runtime iteration count and cost are unknown.");
        }

        var leaders = new HashSet<int> { 0 };
        foreach (var i in decoded)
        {
            if (i.LocalBranchTarget is int target && i.BranchTargetStatus == "local_instruction_boundary" &&
                i.FlowControl != "Call") leaders.Add(target);
            if (i.FlowControl != "Next" && i.Offset + i.Length < codeBytes.Length)
                leaders.Add(i.Offset + i.Length);
        }
        var starts = leaders.Order().ToArray();
        var blocks = starts.Select((start, index) =>
        {
            var end = index + 1 < starts.Length ? starts[index + 1] : codeBytes.Length;
            return new NativeBasicBlock(index, start, end,
                decoded.Count(i => i.Offset >= start && i.Offset < end));
        }).ToArray();
        var blockIds = blocks.ToDictionary(b => b.StartOffset, b => b.Id);
        var edges = new List<NativeFlowEdge>();
        foreach (var block in blocks)
        {
            var last = decoded.Last(i => i.Offset >= block.StartOffset && i.Offset < block.EndOffsetExclusive);
            var flow = last.FlowControl;
            if (flow is "ConditionalBranch" or "UnconditionalBranch" or "XbeginXabortXend")
                edges.Add(new(block.Id,
                    last.BranchTargetStatus == "local_instruction_boundary" &&
                    last.LocalBranchTarget is int target && blockIds.TryGetValue(target, out var id) ? id : null,
                    last.BranchTargetStatus == "local_instruction_boundary" ? "branch" : last.BranchTargetStatus,
                    last.LocalBranchTarget));
            if (flow == "IndirectBranch") edges.Add(new(block.Id, null, "indirect_unresolved", null));
            if (flow is "Next" or "ConditionalBranch" or "Call" or "IndirectCall" or "XbeginXabortXend")
            {
                var next = block.EndOffsetExclusive;
                edges.Add(new(block.Id, blockIds.TryGetValue(next, out var id) ? id : null,
                    next < codeBytes.Length ? flow is "Call" or "IndirectCall" ? "assumed_call_return" : "fallthrough"
                        : "end_of_method_unresolved", next));
            }
        }
        var partial = edges.Any(e => e.ToBlock is null) || decoded.Any(i => i.FlowControl == "XbeginXabortXend");
        return new(method.JitSignature, method.Architecture, "decoded_complete_method_bytes", true,
            Convert.ToHexString(SHA256.HashData(codeBytes)), codeBytes.Length,
            partial ? "normal_flow_partial" : "normal_flow_only", decoded, blocks, edges, sites,
            decoded.GroupBy(i => i.FlowControl).OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count()),
            decoded.SelectMany(i => i.CpuFeatures).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray(), limitations);
    }
}
