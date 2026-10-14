# Investigation priorities and detection evaluation

Open `/priorities.html` (or `/priorities`) on the existing localhost Web host after opening a trusted project in the dashboard. This page groups current Roslyn findings and static sites with the explicitly imported profile. MCP exposes `review_priority_report` and `review_priority_policy`; the Web API exposes `POST /api/priorities/report` and `GET /api/priorities/policy`.

## What the priority means

The engine produces **action order**, separately from diagnostic severity, evidence type and impact. All returned items explicitly have `defectConfirmed=false`.

- **P0**: reserved, never automatically emitted by this version. No trusted reproduction/oracle evidence is consumed.
- **P1**: repair a compiler/configured-analyzer build error, or investigate a frame whose self share and observation count pass the policy. Neither means a production incident or proven wasted CPU.
- **P2**: review warning-level diagnostics, semantic facts, larger static sites, or inclusive-heavy observations.
- **P3**: reference candidates. Low priority is not proof of correctness or low runtime cost.

Defaults are self share >= 10%, >= 20 observations, static site weight >= 40 for P2. They are versioned engineering policy values, **not calibrated precision/recall or statistical confidence**. All are adjustable within explicit bounds. Counts can mean weighted intervals, not independent samples. Thread-time profiles can include waiting; bytes stay bytes and are not converted into CPU time. Static scores are never added to observed weight.

An observed frame is a separate item. A hot method does not automatically promote every source warning or prove which source line caused its cost. Source association is name-based, not verified profile/build/source equality. Unmatched/ambiguous heavy frames remain visible without fabricated source links.

## Duplicate handling, filters and suppressions

Equivalent rule families at the **same project, file, line and column** are joined, retaining original rule IDs and source fingerprints. Unrelated rules/coordinates stay separate. Duplicate profile labels are not summed. Raw loops/local calls alone stay in the static hotspot report instead of filling the actionable queue.

The report preserves input, distinct, duplicate, suppressed, matched and returned counts. Priority counts are before filtering and suppression. It also propagates source/output truncation and compiler error totals.

A suppression needs an exact returned item ID and a nonempty reason (<=500 characters), with at most 200 entries. It affects only the current request, never deletes evidence and can be shown with `includeSuppressed`. The browser keeps edits in its current form only, not persistent storage. IDs are tied to project/path/rule/coordinate (profile IDs include the capture path); do not treat them as global identities across source changes. JSON exports contain paths, evidence and suppression reasons and can be sensitive.

## Expanded semantic boxing detection

`PERF2001` now uses Roslyn boxing-conversion information, rather than only looking for an `object` local/assignment. It covers interface conversions, argument passing, explicit casts and conversions within constructor/accessor loops. Deferred lambda/local-function bodies do not inherit an enclosing loop. Unknown unconstrained-generic cases are intentionally not claimed. This expands one rule; it does **not** complete constructor/accessor/lambda indexing in the separate performance method catalog. A boxing conversion is not proof of actual JIT allocation expense.

Official API: https://learn.microsoft.com/dotnet/api/microsoft.codeanalysis.csharp.conversion.isboxing

## Evaluation boundaries

`DetectionCorpusTests` contains 24 labeled positive/negative C# examples. Labels describe whether a **specific target rule's source pattern** is present, not whether a runtime defect exists. Each example is compiled with Roslyn and matched using the real scanners. JSON output records expected/actual results and source hashes. The set is a small synthetic regression corpus used during implementation, NOT a held-out benchmark or representative real-project precision measurement.

`DetectionEvaluation` computes TP/FP/FN, precision, recall and actual-returned precision@K. It refuses predictions with missing labels, deduplicates predictions and reports null for zero denominators. The corpus's prediction order is fixture order, not product ranking; its top-K metric is not a product top-ten usefulness claim. Separate priority tests verify action ordering, evidence boundaries, matching ambiguity, deduplication, filtering, suppression and truncation.

The dedicated CI runs policy/corpus tests plus real HTTP and MCP integration on Windows and Linux. It uploads only synthetic fixture reports and TRX. No target program, profiler, LLM or Docker experiment is launched by the priority workflow; existing trusted MSBuild/project-analyzer boundaries still apply.

Still needed: representative real-project labels, held-out evaluation, allocation/GC/contention attribution, verified source/build identity, full callable indexing and trusted reproduction evidence for confirmed incident severity.
