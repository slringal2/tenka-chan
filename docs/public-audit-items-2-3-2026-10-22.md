# Public release preflight — items 2 and 3 (2026-10-22)

**Status: BLOCKED / NOT APPROVED for public visibility.**
Read-only GitHub queries and public NuGet package-page reviews were performed.
The repository stayed PRIVATE, and no history, refs, Actions or release settings
were altered. This is an interim audit, not a declaration that code is safe.

## 2. Secrets, Git history and Actions artifacts

### Evidence obtained

- GitHub API reported **353 historical Actions workflow runs**. Sampled run
  `38572257410` has 3 successful jobs (test, docker-smoke and windows-local-jit).
  Its run-artifact endpoint reported zero artifacts **for that selected run**.
  This says nothing about the other runs or retained historical logs.
- GitHub Actions job logs could not be inspected through the connector. They
  must be reviewed using authenticated GitHub UI/API export before public
  visibility changes. Old job logs can contain file paths, process output,
  credentials or code from earlier local/CI contexts.
- We checked the **current HEAD tree** and branch-tip trees of 21 named
  branches, using a filename-only heuristic for `.env`, settings/connection
  descriptors, keys, DLLs, SQLite, dumps and nettrace/ETLX. No matching path
  was found in those selected trees. All 21 API tree responses were complete
  (`truncated=false`).
- These were **only current branch-tip trees**. There are substantially more
  branches, and this check did NOT traverse every historical commit or check
  content for secrets. Deleted/replaced historical files and private PR
  discussions are outside this check. No clearance is granted.
- The latest 50 main-branch commits contain commit author addresses at an
  ordinary personal-mail domain rather than exclusively GitHub noreply.
  The values are deliberately not copied here. Before public publication
  review all commit-author and committer identity metadata and decide whether
  disclosure is acceptable. Changing Git settings now will not change history.

### Blocking follow-up

1. Run `scripts/check-public-readiness.ps1` and optional
   `scripts/public_release_preflight.py` on a **fully fetched clone**,
   inspect all available refs, and triage warnings privately.
2. Run a trusted independent historical secret scanner over all reachable
   commits/refs (e.g. Gitleaks), plus attachments, logs and GitHub
   issue/PR discussions. Rotate leaked credentials before considering any
   history rewrite. Do not post raw matches into issues or reports.
3. Check all **353 workflow runs**, especially logs and artifacts that GitHub
   may expose upon visibility change. Do not assume a successful run is free
   from leaked credentials. Document inspected IDs/time ranges, not raw tokens.
4. Review author emails/identity and third-party/workplace project references.
   If a private repository contains sensitive history, prefer a separately
   reviewed source-only repository instead of silently making existing history
   public; owner decides, with explicit approval.

## 3. Licenses, dependencies, ownership

### Evidence obtained

- Root `LICENSE` contains standard MIT terms for project-owned source,
  copyright `2026 slringal2 and contributors`.
- Public NuGet Gallery pages independently confirm:
  - `ModelContextProtocol 2.2.0`: **Apache-2.0**:
    https://www.nuget.org/packages/ModelContextProtocol/2.2.0
  - `SQLitePCLRaw.lib.e_sqlite3 2.1.13`: **Apache-2.0**:
    https://www.nuget.org/packages/SQLitePCLRaw.lib.e_sqlite3/2.1.13
- These licenses do **not** block MIT licensing of Tenka's separately authored
  own code, but the packages are not re-licensed as MIT; redistribution may
  require preserving their license/NOTICE texts. Native SQLite may have other
  applicable notices.
- `THIRD-PARTY-NOTICES.md` lists direct dependencies and upstream references;
  it is **not** proof that all resolved transitive dependencies are licensed
  compatibly or that runtime artifacts are cleared for distribution.
- `scripts/collect-package-licenses.py` on main provides an offline,
  constrained inventory based on actual `project.assets.json` plus explicitly
  allowed NuGet cache. Its synthetic tests ran **27/27**; that is NOT a scan of
  actual restored Tenka dependency assets, and `publicReady=false` remains
  hardcoded deliberately.
- ILSpy is fetched separately into git-ignored `samples/External` by a
  pinned recipe; do not repackage that checkout or claim ownership of upstream
  files. ILSpy's project MIT license does not automatically cover its bundled
  third-party assets or logos.

### Blocking follow-up

1. On the user's actual restored .NET environment, run the offline
   NuGet license inventory for **production AND sample/test project** assets,
   then review unresolved entries and the **actual** bundled notices.
2. For publication of **source only**, do not include NuGet packages, ILSpy
   checkout, binaries, sample model weights or fetched assets in the Git repo.
   Binary releases need a separate resolved redistribution license review.
3. Verify employment contracts, employer resource/code ownership,
   coauthor/third-party contributions, project name/character artwork and
   trademarks. Those rights cannot be inferred from GitHub/NuGet metadata.
4. Independently verify packaging and any dependency-security advisories.
   Public NuGet license declarations are not a dependency-vulnerability audit.

## Overall

**Item 2: RED / BLOCKED.** Historic code/log review incomplete and 353 Actions
runs present; possible publication of non-noreply commit identities.

**Item 3: AMBER / PARTIAL.** Two important NuGet direct-license declarations
confirmed and existing inventory tooling, but actual transitive package
licenses and ownership/branding sign-off still unverified.

**Decision: remain PRIVATE.** No code, workflow or visibility change was made
by this audit. This report intentionally contains no credential values,
email addresses or private code. It is not a legal opinion.
