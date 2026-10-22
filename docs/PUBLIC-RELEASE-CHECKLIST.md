# Public-release gate (manual approval required)

**Status: preparation only. The repository remains PRIVATE.** This document
is not a security certification, legal opinion or authorization to change
GitHub visibility. Mark items as verified only with evidence from the intended
release commit, not tests run against an earlier main.

## License, contributors and branding

- [ ] Confirm that `slringal2 and contributors` accurately identifies the
  actual rights holders in [LICENSE](../LICENSE). Check employment agreements,
  employer resources, coauthors and copied code. Get permissions as needed.
- [ ] Approve **MIT** for the project-authored code deliberately, with the
  understanding that anyone can use it commercially, fork it or redistribute it
  while retaining the notice. MIT does **not** oblige downstream changes to
  remain open source.
- [ ] Validate every direct and **transitive** NuGet package, including its
  actual installed version and NOTICE requirements; see
  [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).
- [ ] Verify the license and rights for logos, character names, screenshots,
  icons, fonts and other non-code assets. A source license does not grant rights
  to third-party trademarks or characters.
- [ ] Keep externally fetched **ILSpy** in git-ignored `samples/External/`;
  if distributed separately, include ILSpy and its third-party licenses.

## Repository history and credentials — mandatory before making public

- [ ] Run `powershell.exe -NoProfile -ExecutionPolicy Bypass -File
  scripts/check-public-readiness.ps1` on a **complete local clone** with all
  intended Git branches/tags fetched. Triage all flagged paths and commits.
  It is a heuristic, not a full secret scanner.
- [ ] Use a dedicated secrets scanner (for example Gitleaks or equivalent)
  over **all commits and refs**. Review historical actions logs, docs,
  issue/PR discussions, attached artifacts, release assets and author emails.
- [ ] Revoke/rotate any leaked token **before** considering history cleanup.
  Removing a secret from HEAD does not remove it from prior commits or pull
  request diffs. Decide whether a fresh, audited public repository (with
  intentionally curated initial history) is safer than converting this private
  repository as-is. Do not force-push or rewrite history automatically.
- [ ] Ensure local `mcp-config.json`, launcher `settings.json`, connection
  descriptors, SQLite records, trace files, generated benchmarks, source
  projects, logs, API tokens, internal addresses and confidential materials
  are absent from **all publishable refs**. Review `.gitignore`; note that it
  does not erase a previously committed secret.
- [ ] Review `.github/disabled-workflows/` and all historical branches.
  Owner policy is **no active CI**. Disabling Actions in repository settings
  is an additional control if desired; a paused current-main workflow
  directory alone does not guarantee no workflows exist in other refs.

## Release correctness and user safety

- [ ] Close any critical outstanding PR conflicts, or explicitly defer them.
  In particular, the separate dynamic-analysis **PR #32** remains draft until
  its own build/Windows EventPipe integration verification is complete.
- [ ] On a clean Windows 11 checkout with installed .NET 10 SDK, run a fresh
  `dotnet build` for the Web/MCP projects and **full** tests for
  `tests/TenkaChan.Tests` and `tests/Chan.Core.Tests`.
- [ ] Explicitly check GitHub Actions remain paused, and do not introduce
  Docker or remote cloud execution as a requirement.
- [ ] Verify `setup.bat`, `start.bat`, external stdio MCP registration,
  connection authentication, source read/reload, review history/revisions and
  denial of unapproved MSBuild/host execution on Windows PowerShell 5.1.
- [ ] Exercise at least a bounded real project and a 3-project ILSpy subset
  using locally approved operations. Document failures and limits.
- [ ] Check the accuracy of README, `docs/security.md`, THIRD-PARTY-NOTICES
  and user-facing claims. Provide an explicit **experimental/preview** label
  and do not claim benchmarks prove speedups or code correctness.
- [ ] If publishing **binaries or packages**, add actual redistributed
  dependency license texts/notices, a resolved dependency list and artifact
  provenance. A GitHub source-only MIT license is not a binary license audit.

## The visibility decision

Only after the above review should the repository owner explicitly change
visibility in GitHub Settings. The default recommendation is **publish a
source-only preview first** (no binary release), while documenting
limitations, required per-operation approvals and paused CI.

**No automated change to repository visibility or its commit history is
authorized by this checklist.** For GitHub's guidance on leaks and history
rewrites see
[Removing sensitive data from a repository](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/removing-sensitive-data-from-a-repository).
