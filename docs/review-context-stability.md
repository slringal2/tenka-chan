# Review context stability and diagnosis

> 更新: ユーザーから同一構成ビルド＋再ロード後の安定を確認したとの報告あり。詳細と生ログの有無は [実機フィードバック](validation-feedback-2026-10-17.md)。旧記録の根因特定とは区別します。

## Scope and verification status

Base: `012ee82f1241858c46a9004cb05358b2aeec05f8` (includes the user's `TrackedReviewTarget` alias fix).

The user reports real `contextHash` churn, but the affected Windows snapshots and checkpoint database are not available in this authoring environment. **The exact cause of that incident is not established by this patch.**

The inspected v1 algorithm hashes `MetadataReference.Display`, so the same module at a different reference path produces a different context hash. An arithmetic-only Python reproduction, not a C#/Roslyn run, confirmed this trigger. The authored C# regression `ReferencePathOnlyChurnIsReproducedByV1AndRemovedByV2` exercises the actual `SourceExplorerIndex.ReviewCatalogAsync` with in-memory copies of the same PE metadata at different display paths.

**C# build and all 38 new xUnit cases remain unexecuted here.** `dotnet --info` failed with exit 127; installation was attempted but DNS resolution for the SDK download failed. The user's prior 583/583 result does not validate these new changes. CI remains paused. No permissions or session/workspace ownership rules change.

## Changes

`contextHash` uses a version-2 fingerprint of the loaded project and its dependency closure. New checkpoints also store seven fixed-size component digests:

| Component | Inputs |
| --- | --- |
| `sourceFiles` | All other loaded source documents, canonical paths and content hashes |
| `generatedSources` | Loaded files ending in `.AssemblyInfo.cs`, `.AssemblyAttributes.cs`, `.GlobalUsings.g.cs`, with paths and full content hashes |
| `projectFiles` | Explicit project file content hashes |
| `parseOptions` | Existing language version, source kind, documentation mode and preprocessor symbols |
| `compilationOptions` | Existing output kind, optimization, platform, unsafe, overflow and nullable settings, plus assembly name |
| `metadataReferences` | Loaded module MVIDs, reference kind, aliases and EmbedInteropTypes |
| `projectReferences` | Canonical referenced-project paths, aliases and EmbedInteropTypes |

Generated-file classification is a filename heuristic for **diagnosis**, not an exclusion rule. An informational-version or other generated-content change still changes the context. So do edits to other methods in the project or source dependencies. The original conservative scope is intentionally retained.

Reference identity is read from metadata already held by Roslyn, rather than reading arbitrary file paths or loading an assembly for execution. Reference display-path relocation alone is no longer an input. MVID is the ordinary compiler's module-version identifier, **not a cryptographic content or authenticity proof**. Existing execution authorization, IL/PDB and assembly SHA-256 checks remain unchanged.

References and input fragments are normalized before deterministic ordering; transient Roslyn project IDs and reference object identities are not persisted. Root project paths and document paths still matter; moving a checkout is not promised to retain target IDs or checkpoints.

Unsupported/unreadable reference metadata and unresolved dependency projects produce an incomplete context. Such targets remain inspectable but cannot be marked current/checked on the strength of an incomplete fingerprint. Resolve the intended references and reload; no automatic restore/build or approval is performed.

## How to diagnose the user's churn

Use `tenka_review_target` or `tenka_review_context` for the same intended target. Read:

- `target.contextHash` and `target.contextFingerprint` for the currently loaded context.
- `state.reason`, `state.sourceChanged`, `state.contextChanged`, `state.requiresReload`.
- `state.contextChange.status` and `state.contextChange.changedComponents`.
- The checkpoint fingerprint and hashes in saved history.

Queue responses deliberately omit the component manifests and detailed context-change object to avoid repeating large diagnostics on every row. Their status, reason, coverage and cursor semantics remain intact. Detailed calls and persisted history retain diagnostics. Response shaping copies records; it does not erase stored data.

For **one genuinely inspected target**, record a fresh checkpoint, then compare after these operations separately:

1. Reopen unchanged source several times without building or editing.
2. Build the same configuration and reopen.
3. Switch configuration and reopen.
4. Relocate the same dependency binary without changing its bytes and reopen.
5. Edit another method or dependency and reopen.

Do not combine all operations into one step: that obscures the triggering input. `generatedSources` after a build points to generated-file content; `compilationOptions` after a configuration switch is an expected invalidation; `metadataReferences` points to module version/properties rather than its display location. These are categories, not proof of a particular offending file.

The fingerprint is of the **loaded snapshot**. It does not continuously rehash files or re-evaluate arbitrary imported MSBuild targets, SDK selection, source generators, environment variables or runtime state. A source's stale-file detection retains its existing behavior. This patch does not promise exhaustive semantic equivalence checking.

## Existing checkpoint compatibility

No database reset, target-ID change, bulk status rewrite or history deletion occurs. JSON metadata is optional, so old records deserialize without a schema migration.

For an old checkpoint without component metadata, the exact old v1 hash is also computed from the same loaded inputs. If it still matches, `state.contextChange.status=legacy_exact_match` retains the checkpoint under **the original v1 comparison scope**. This is not evidence that the old checkpoint covered the new v2 reference checks. The old checkpoint is not rewritten by reading it.

If the legacy aggregate hash does not match, `legacy_components_unavailable` is returned. A single aggregate hash cannot reveal whether a change was only a path or meaningful source/options. The body being unchanged is not enough to restore `checked` automatically. Reinspection and one new explicit checkpoint are required to establish a v2 baseline. Already-mismatching legacy records are therefore **not retroactively repaired by this patch**.

New v2 checkpoints never fall back to legacy equality. Their full fingerprint must match. Source changes, stale snapshots and explicit `unreviewed` resets still take precedence. Unchanged v2 context after a new checkpoint is not permanently `needs_review` simply because an older checkpoint differs.

## Regression tests

```powershell
dotnet test tests/TenkaChan.Tests/TenkaChan.Tests.csproj -c Release --filter "FullyQualifiedName~ReviewContextFingerprintTests|FullyQualifiedName~ReviewMetadataFingerprintTests|FullyQualifiedName~ReviewContextChurnTests"
dotnet test tests/TenkaChan.Tests/TenkaChan.Tests.csproj -c Release
dotnet build src/TenkaChan.Web/TenkaChan.Web.csproj -c Release
```

38 authored cases: 31 Facts and 7 InlineData cases. They cover actual Roslyn catalog wiring, persistence to a temporary SQLite database, legacy JSON compatibility, true context changes, unresolvable references, checkpoint re-recording and bounded queue responses. No tests use the user's real checkpoint database. **Authored case counts are not pass counts.**

## Not changed

The single shared workspace, session-local experiment/evidence ownership and `MaxSessions=16` remain. `checked`/`optimized` are still agent work records, not correctness or performance verification. No claim is made that the user's usability score is now higher or that every source of churn has been eliminated.

Roslyn API references:
- https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.portableexecutablereference.getmetadata
- https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.modulemetadata.getmoduleversionid
