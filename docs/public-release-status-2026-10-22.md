# Public-release preparation status — 2026-10-22

PR #34 prepares the original project source for MIT distribution. Repository
visibility is deliberately unchanged: **license adoption is not publication**.
The original MIT text does not replace third-party licenses or establish rights
to employer/coauthor/branding content that the author does not own.

## Changes beyond the initial documentation PR

### Review-queue regression fixed before publication

The merged review-DX changes accidentally left `ReviewQueueViews.ValidateFilters`
calling itself unconditionally. Because the gateway invokes it before building
the queue, this could overflow the call stack even with valid inputs.

The branch now implements the validation once in `ValidateFilters`, with the
page builder and gateway both using that implementation. Regression tests call
the gateway's validator directly for all 18 supported kind/state combinations
and five invalid pairs. Existing assertions about the queue's note were updated
to permit the previously added workflow advice rather than require exact old text.
These xUnit cases were **added, not executed in this editing environment**.

### Public-readiness history inspection improved

`scripts/check-public-readiness.ps1` now:

- rejects shallow and partial clones, missing reachable objects, and replacement
  refs instead of returning reassuring no-match results for incomplete history;
- uses `git log --all --full-history -m` so merge-only changes are inspected,
  including patterns no longer present on the current branch;
- disables external diff/text-conversion helpers for history inspection;
- keeps optional Git locking off and disables lazy fetching during the check,
  restoring the previous environment values afterward;
- reports counts rather than token values or private email addresses;
- retains explicit limitations: local refs only, no Actions/PR attachment audit,
  no proof of rights, no complete semantic secret detection, and no permission
  to change repository visibility.

Run from a fully fetched, trusted checkout:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/check-public-readiness.ps1
```

The script never fetches missing history or changes GitHub visibility. If it
finds incomplete history, prepare a complete clone separately with authorization
and run it again. A successful exit is not a public-release approval.

## Tests actually executed here

```text
Python 3.13.5 / Git 2.47.3 / Debian 13
python scripts/test-public-readiness-git.py
11 tests passed
```

Those tests read the literal Git argument lists from the PowerShell script and
run them against **disposable synthetic repositories**, not the user's repository.
They cover clean history, current/deleted token patterns, unmerged branches,
tag-only commits, a merge-only token, shallow/partial-clone detection, external
helper nonexecution, unchanged index/refs, and command safety properties.

The merge-only test also demonstrates the old command missed that case. This
is a real Git regression test, not a string-count or brace-balance check.
It is **not** an execution of Windows PowerShell 5.1 or the whole `.ps1` script.
Python remains optional developer-test tooling; normal Tenka operation does not
acquire a new Python dependency.

## Not yet verified

- Full C# build and xUnit at the final merge commit, including the filter fix.
- Windows PowerShell 5.1 parser/execution and the normal launcher/MCP workflow.
- Secrets across the actual repository's complete history, remote/PR refs,
  historical Actions logs/artifacts, and discussions.
- A restored transitive package/license inventory, binary notices, and security audit.
- Ownership/employment/coauthor/branding rights and release approval.

The editing container has neither .NET SDK nor PowerShell. An attempt to reach
the official PowerShell binary distribution also failed DNS resolution. This
limitation does not turn unexecuted tests into passing tests. Prior 759/759
results belong to earlier user-run commits and are not results for this PR.

Recommended Windows checks, with the running host stopped or using Debug:

```powershell
dotnet build src/TenkaChan.Web/TenkaChan.Web.csproj -c Debug --no-restore
dotnet test tests/TenkaChan.Tests/TenkaChan.Tests.csproj -c Debug --no-restore
dotnet test tests/Chan.Core.Tests/Chan.Core.Tests.csproj -c Debug --no-restore
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/check-public-readiness.ps1
```

Review every preflight warning locally. Do not paste credentials into issues.
Use [PUBLIC-RELEASE-CHECKLIST.md](PUBLIC-RELEASE-CHECKLIST.md) for the complete
manual gate. CI remains paused, Docker is not added, and PR #32's separate
dynamic-analysis work is not silently merged or advertised as validated.
