# Measured-first performance evidence (.NET 8+)

This workflow brings **representative workload observations**, **Roslyn static
cost candidates**, **RyuJIT machine code**, and **captured IL/PDB source spans**
into a single investigation without inventing relationships.

## Observe, then investigate

Open a trusted project with `workspace_open` and import a real Speedscope
sampled or evented profile using `performance_profile_import`. A profile can
come from dotnet-trace or a compatible profiler. The importer itself never
launches target code.

Call `performance_evidence_triage(maxObserved,maxStatic,jitExperimentId)`:

- **Observed** frames are ranked by **exclusive/self weight** and retain
  the profile's unit (milliseconds, seconds, bytes, or unitless).
- **StaticOnly** entries are Roslyn suspects not linked to the measured
  profile. They are sorted by static heuristic score, never added to
  sampled weight or declared to be actual bottlenecks.
- **Unmatched/ambiguous** frames remain separate. Lack of a unique link
  does not warrant displaying arbitrary source code or claiming low cost.
- Passing a completed `kind=jit` experiment ID adds optional JIT method
  summaries with signature-level correlation. This does not establish
  that the loaded profiler trace came from the same binary build.

Next call `performance_method_dossier(methodId,...)`. If available, select
a captured `binary-*.dll` and MethodDef token from
`jit_binary_files` / `jit_binary_methods`; use the same JIT experiment ID
and pass `assemblyFile`, `methodToken` and optionally `sourceFilePath`.
The dossier verifies saved artifact hashes and rejects mismatched native
method names, declaring types or supported parameter signatures.
It uses Portable PDB mapping for **IL-to-source** ranges, checks PDB build
identity, and verifies the optional current-source checksum before
showing a C# excerpt.

**There is no native-instruction-to-IL offset mapping in Portable PDB.**
The native listing and IL/source mapping remain separately sourced
evidence even when they refer to the same named method.

## Browser

The **性能ホットスポット** view now includes
**実測優先の根拠レポート** and **メソッド別の詳細調査**.
You can import a profile, optionally select a completed JIT experiment,
view observed/static rankings separately, and inspect native instructions,
captured IL cost sites and PDB/source checksum status.

Local HTTP API (CSRF required for POST):

- `GET /api/hotspots/triage`
- `POST /api/hotspots/investigate`

Example investigation JSON:

```json
{
  "methodId": "exact-Roslyn-method-ID",
  "jitExperimentId": "completed-JIT-experiment-ID",
  "assemblyFile": "binary-App.dll",
  "methodToken": 100663297,
  "sourceFilePath": "C:\\Projects\\App.cs",
  "maxInstructions": 1200
}
```

The assembly/token fields are optional together; an arbitrary token is
never auto-associated with an observed frame.

## Interpretation and trust

- Thread-time samples and evented elapsed intervals can include waiting,
  blocking, scheduler time and other overhead. "Self 60%" is **not
  necessarily** 60% of CPU execution time.
- Static risk, code size, opcode counts, IL allocations and dynamic profile
  weights are incomparable units. A code pattern can motivate a benchmark,
  not establish a speedup.
- Runtime, tiered JIT/PGO, CPU architecture, workload and source revision
  affect results. The analyzer host remains .NET 10; detached experiments
  explicitly target .NET 8+ with matching SDK Docker images.
- A PDB identity match is a build-consistency check, not a signature against
  forged files; a name-based profiler link is weaker than a verified PDB.
- Only previously collected evidence is read. Generated code execution
  remains gated behind the existing explicit Docker opt-in.

See `docs/binary-source-evidence.md` for capture and source-checksum details.
