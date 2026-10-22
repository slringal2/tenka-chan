# Optional Git-blob release preflight — 2026-10-22

This supplements `scripts/check-public-readiness.ps1`; it does not replace the
PowerShell improvements already on the branch. Python is **optional developer
verification tooling only**. Normal Tenka-chan Web/MCP use still does not require
Python, Node, Docker, or a hosted CI service.

## What this additional check does

`public_release_preflight.py` reads committed Git objects reachable from the
**locally available refs**, not only the current working tree or changed lines.
It checks deleted-file content and other local branches, reports common credential
patterns without the actual values, and records exact scan coverage/limits.

- A shallow clone, partial-clone configuration or replacement refs makes coverage
  incomplete. Replacement-object interpretation and lazy network fetches are disabled.
- Object and byte limits, unavailable objects and Git LFS pointers are not
  reported as clean content. Unknown content needs separate inspection.
- The index, refs and working tree are not modified. No network requests,
  builds, script hooks, external diff helpers or visibility changes are performed.
- `.env.example` is permitted as a filename, but its content is still scanned.
- Author email addresses and matching credential values are not printed.
- Required release documentation must be committed, not merely present locally.

Run in a trusted, fully fetched repository with Python 3.10+ and Git:

```powershell
py -3 scripts/public_release_preflight.py --repo . --json
```

The existing PowerShell entry point remains available without Python:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/check-public-readiness.ps1
```

Exit codes are `0` (no configured heuristic findings), `1` (manual review items),
and `2` (blocker, failure or incomplete coverage). **`publicReady` is always false.**
These checks cannot approve licensing, author rights, dependency security or
publication. They do not inspect GitHub Actions logs, remote refs absent locally,
PR/issue discussions, commit/tag messages, ignored files or submodule content.
Use a dedicated secret scanner and the complete
[public-release checklist](PUBLIC-RELEASE-CHECKLIST.md) as additional gates.

## Verification actually performed for this addition

```text
python3 -m unittest discover -s tests -p 'test_public_release_preflight.py' -v
17 tests passed
```

These were executed with real Git commands in disposable **synthetic local
repositories**, not against the complete user repository history. Cases cover
current/deleted secrets, another branch, safe and unsafe `.env.example` content,
shallow/partial clones, replacement refs hiding an original secret, size/object
budgets, LFS pointers, forbidden tracked paths, uncommitted documentation,
unchanged index/refs/worktree, private output redaction, and unavailable repositories.
The scanner and test content uploaded to GitHub match the tested local Git blobs:

- Scanner: `2b46b3df9b332f670bd89a4b1c9855303eafc959`
- Tests: `5df6e5fd3a886c238e01986342cf5f0a4cd1d953`

## Separate C# release blocker fixed in this PR

`ReviewQueueViews.ValidateFilters` previously called itself unconditionally.
It now contains the actual kind/state validation and is shared by the gateway
and queue builder. Tests now call that validator directly for 18 valid combinations
and five invalid combinations. Two existing exact-note assertions were adjusted
for the additive review-workflow note.

Those **C# xUnit tests have not been executed in this editing environment**:
.NET SDK and PowerShell are unavailable, and downloading the SDK failed DNS
resolution. The 17 Python/Git passes do not imply a successful .NET build.
Run the full Windows build and xUnit suites before a public preview. Earlier
759/759 results belong to older user-run commits, not this release candidate.

MIT license adoption and merging this preparation are separate from changing
GitHub visibility. The repository remains private until the owner completes the
public-release gates. CI remains paused, and dynamic-analysis PR #32 is not
silently merged by this work.
