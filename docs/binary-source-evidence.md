# Build-identified IL and source evidence (.NET 8+)

## What this adds

The JIT experiment now preserves the DLL/PDB files from its own build output,
records SHA-256 hashes and `dotnet --info`, and exposes those artifacts to the
reviewer. This avoids accidentally analyzing a later host build while discussing
an earlier JIT listing. Capture is bounded (60 files, 32 MB per file, 96 MB total);
`BinaryCaptureTruncated` explicitly reports an incomplete capture. Framework
runtime assemblies outside the harness output are not included.

Portable PDB sequence points map **IL offset ranges** to C# source spans. The
PE CodeView GUID/stamp must match the PDB debug identity. Both external and
embedded Portable PDBs are supported. Windows/native PDB files are not.
A wrong PDB is rejected instead of producing plausible but incorrect line numbers.
PDB identity is an ordinary build-consistency check, not authentication against
an attacker who deliberately forges a PE and PDB.

A source file is read only when the caller supplies its path and the original
allowlist permits it. The bytes must match the PDB document's SHA-1/SHA-256
checksum before any excerpt is displayed. On mismatch, the historical PDB span
remains available with `checksum_mismatch`, but current source text is withheld.
Document paths, Source Link URLs, and symbol-server links in a PDB are **not
followed**. Hidden sequence points remain explicitly unmapped.

## MCP workflow

For a completed .NET 8+ JIT experiment:

1. `jit_binary_files(experimentId)` lists captured `fileName` values and hashes.
2. `jit_binary_methods(experimentId, assemblyFile, query, limit)` lists full
   signatures and MethodDef tokens in that exact DLL. Use `binary-YourApp.dll`,
   not a later `bin/Release` build from the host.
3. `jit_source_inspect(experimentId, assemblyFile, methodToken,
   sourceFilePath, maxInstructions)` decodes IL, resolves call/field/type
   operands, verifies the associated PDB and optional source checksum, and
   returns source-linked IL cost sites. `sourceFilePath` is optional.
4. `native_disassembly_result(experimentId)` remains the separate native
   machine-code view; use runtime profiling/BenchmarkDotNet to determine cost.

For an existing allowlisted DLL:

- `assembly_methods(assemblyPath, query, limit)` returns overload signatures,
  MethodDef tokens, assembly SHA-256, and module MVID.
- `binary_source_inspect(assemblyPath, methodToken, pdbPath, sourceFilePath,
  maxInstructions)` performs the same IL/PDB analysis without executing the DLL.
  The PDB may be supplied explicitly, found beside the DLL, or embedded in it.

Metadata tokens are scoped to an assembly build. Never reuse a token against
another SHA-256/MVID without resolving it again. The older `il_inspect` tool now
rejects ambiguous overloads instead of silently choosing the first one.

## Reliability fixes to native analysis

Previously, skipping an unrecognized mnemonic could shift all later byte
positions. Now all syntactically recognized instruction listings are retained,
including unclassified mnemonics. Offsets are returned only when all instruction
encodings are present and their total length agrees with the JIT code-size
footer. Otherwise all native offsets are null and `OffsetValidation` explains
why. This validates listing completeness, not the correctness of an independent
ISA decoder (the tool parses RyuJIT's own disassembly text).

Scalar AVX operations such as `vaddsd` and vector-state operations such as
`vzeroupper` are no longer counted as packed SIMD operations. Helper names in
data operands alone do not become allocation-call evidence.

Native comparisons now require complete byte-verified instruction listings,
matching architecture, matching JIT tier/mode, and unambiguous counterpart
methods. Missing pairs and comparison limits are explicit. Even a valid
comparison is **code-generation evidence, not a speedup or regression verdict**.

## Execution semantics

An experiment ID is single-use once execution is attempted. A durable atomic
reservation blocks simultaneous execution and prevents a new run from silently
reusing old TRX/JIT artifacts. Prepare a new experiment for retries or repeated
measurements. Existing saved reports remain readable. An interrupted experiment
also requires a fresh ID; its marker is not automatically deleted.

## Deliberate boundary: native instructions are not IL offsets

This feature provides two verified views:

- JIT native instruction encodings with validated byte positions;
- captured assembly IL offsets with Portable PDB source ranges.

It does **not** claim a one-to-one native-instruction-to-C# correspondence.
Optimized code, inlining, code movement and multiple JIT versions require a
native-to-IL map from the same runtime/code version. A PDB alone cannot supply
that map. Method-name correlation remains weaker than build-verified IL mapping.

## Validation

Tests emit real PE files with external and embedded Portable PDBs and exercise
source checksum mismatches, wrong PDB identity, hidden ranges, overloads,
invalid branches, truncation, unrecognized machine instructions, mixed JIT tiers,
artifact replacement and repeated-execution rejection. The opt-in SDK8 Docker
smoke executes real .NET 8 code, captures its binaries/PDB and native listing,
then verifies IL-to-source mapping against the source fixture. Regular unit
tests do not imply that a real Docker workload ran; the Docker CI job performs
that separate integration check.

Primary format/API references:
- https://github.com/dotnet/runtime/blob/main/docs/design/specs/PortablePdb-Metadata.md
- https://learn.microsoft.com/en-us/dotnet/api/system.reflection.portableexecutable.pereader.readcodeviewdebugdirectorydata
- https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata.methoddebuginformation.getsequencepoints
