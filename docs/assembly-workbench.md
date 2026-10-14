# Assembly workbench and source callable coverage

Open `/assembly.html` or `/assembly` on the running localhost Web host.

## Managed assemblies

Read an allowlisted DLL/EXE without loading it for execution. The browser exposes
existing PE metadata, full MethodDef signatures, assembly SHA-256/MVID and IL/PDB
inspection. Select a method from the catalog; the browser passes its DLL hash
back to reject a stale token after a build change. Portable PDB source locations
are kept separate from native instructions. A source file is read only when
explicitly supplied and its excerpt requires a matching checksum.

## Native code

Choose exactly one already-saved Docker JIT experiment, trusted local JIT capture,
or an allowlisted `.txt` RyuJIT listing, then select an explicit zero-based body
index. `assembly_native_analyze` decodes complete contiguous x86/x64 instruction
bytes using pinned Iced 1.21.0. It reports decoded instructions, CPUID requirements,
read/write registers and memory, basic blocks and normal control-flow edges.
No target is run, compiled, patched or profiled by this tool or page.

Invalid bytes, unsupported architectures, inconsistent instruction boundaries,
missing encodings, truncated methods and size mismatches are explicit refusal
states. ARM64 listings can still be viewed with existing tools but this independent
byte decoder is x86/x64-only. P2 investigation sites cover integer division,
synchronization/fences, indirect calls and backward-branch candidates. These are
not confirmed defects, measured latency, spills, missed vectorization or proof of
wasted CPU. External/indirect branches and instruction-interior targets remain
unresolved. Exception paths and non-returning calls are not reconstructed.

`assembly_native_compare` compares two explicitly selected bodies. Code-size,
instruction and CPUID differences are code-generation evidence only, not a speedup
or semantic-equivalence verdict. Matching reported environment labels are weaker
than CPU/runtime-patch/workload/build equality. Relative offsets start at zero;
formatted external targets are not process addresses. Saved listings are unsigned.

The same read-only features are exposed under CSRF-protected POST endpoints:
`/api/assembly/methods`, `/api/assembly/il`, `/api/assembly/native`,
`/api/assembly/compare`. The existing `assembly_methods`/`binary_source_inspect`
MCP tools remain available. Original local execution trust gates are unchanged.

Iced project and API examples: https://github.com/icedland/iced/tree/v1.21.0/src/csharp/Intel

## Expanded callable catalog

Roslyn indexing now includes explicit constructors/static constructors/destructors,
property/indexer/event accessors, expression getters, operators/conversions,
local functions and anonymous functions. Method summaries expose `callableKind`.
Static cost sites, inspection and deterministic finding ownership use this catalog.
Deferred bodies do not inherit enclosing loops or double-count their operations in
the enclosing method. Nested function CFGs are withheld instead of returning the
parent's graph. Generated-name profile/JIT mapping for lambdas/local functions is
not guessed; they remain candidates without automatic native correlation.

Still incomplete: primary-constructor/field-initializer lowering, implicit/generated
bodies, async state-machine/inlining maps, full dynamic/property/constructor call
edges, exact native-to-IL/C# mapping, CPU/wait/GC attribution and representative
held-out real-project detection evaluation. Test fixtures are controlled synthetic
cases, not a real-world defect-detection accuracy benchmark.
