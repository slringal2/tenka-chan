# Architecture and source organization

Tenka-chan is not an embedded LLM. It is a **local evidence provider** used
by a connected, external MCP agent. The Web frontend displays workspace
state, review checkpoints, analysis artifacts and authenticated agent activity.

```text
External MCP client (model/instructions owned by client)
    |
    v
src/TenkaChan.Mcp/Agent          stdio bridge, session/error/result contracts
    |
    v
src/TenkaChan.Web               loopback HTTP, approved gateway, workspace gate, UI
    |
    +--> src/TenkaChan.Mcp/Analysis
    |       Roslyn solutions, deterministic source/IL/runtime candidate joins
    |
    +--> src/TenkaChan.Mcp/Tracking / Review
    |       durable targetId, source/context hashes, revisions and SQLite receipts
    |
    +--> src/TenkaChan.Mcp/Experiments
    |       benchmark/testing, trusted local execution, traces/artifacts
    |
    +--> src/TenkaChan.Mcp/Security
            explicit allowed paths, trust checks, fixed sample discovery

src/Chan.Core                    language-neutral validation/helpers (net8.0)
samples/                         first-party learning fixtures and external recipes
tests/                           unit, synthetic integration and opt-in host tests
scripts/                         Windows launcher, local checks and sample recipes
docs/                            design, security and operator guidance
```

## Trust and data ownership

1. The agent's own reasoning is untrusted client text. Tool results are
   **observations**, not approval to execute.
2. The Web server has one shared loaded Roslyn workspace per running instance.
   Selection IDs (`typeId`, `methodId`, `snapshotId`) are in-memory. Durable
   review `targetId`, hashes and optimistic revisions are separate state.
3. Read-only analysis should not start a build or target process. Building a
   trusted project can execute MSBuild targets even during workspace load.
4. Generated tests/benchmarks and JIT/trace target execution use distinct host
   permission switches, explicit per-call or scoped approval and bounded
   artifacts. The default Windows launch does **not** require Docker.
5. Runtime evidence, CPU/GC/allocation samples and native instruction counts
   do not automatically prove a method defect or a performance regression.
6. Limits and truncation are part of the API contract. Never infer complete
   analysis when a source/file/index/result page was omitted.

## Editing guide

- `Analysis`: pure parsing/classification and Roslyn indexing. No external
  model calls or arbitrary command execution.
- `Tracking`: append-only checkpoints, conflict/invalidity rules and
  context-fingerprint compatibility. Do not silently revise old claims.
- `Experiments`: process launching and report provenance; changes require
  trust-gate and cancellation tests.
- `Agent`: MCP response surface, durable investigation ownership and bounded
  result transport. Keep public tool parameter names stable.
- `Web`: session-gated action routing, no browser-side secret material.

This is a current architectural guide, **not an API stability guarantee**.
The project is still experimental; see the root README and verification logs.
