#!/usr/bin/env python3
"""Read-only local Git preflight; never a license/secret/publication clearance.

Python 3.10+, standard library and Git only. Does not fetch, upload, change
refs, run hooks/builds, or print matching secret values. The raw repository
must be trusted; this is not a hostile-input sandbox or a complete DLP tool.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
from typing import Any

REQUIRED = (
    "LICENSE", "THIRD-PARTY-NOTICES.md", "SECURITY.md", "CONTRIBUTING.md",
    "docs/PUBLIC-RELEASE-CHECKLIST.md",
)
PATTERNS = {
    "github_token": re.compile(rb"(?:gh[pousr]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{30,})"),
    "aws_access_key": re.compile(rb"AKIA[0-9A-Z]{16}"),
    "api_token": re.compile(rb"sk-[A-Za-z0-9_-]{32,}"),
    "private_key": re.compile(rb"-----BEGIN (?:[A-Z ]+ )?PRIVATE KEY-----"),
}
SENSITIVE = re.compile(
    r"(?i)(?:^|/)(?:mcp-config\.json|settings\.json|\.env(?:\..*)?|"
    r".*-connection\.json|.*\.(?:pfx|p12|pem|key|sqlite|sqlite3|db|dmp|nettrace|etlx|nupkg|dll|exe))$"
)


class PreflightError(RuntimeError):
    """An unavailable or incomplete check, not a successful clean result."""


def git(repo: Path, *arguments: str, data: bytes | None = None) -> bytes:
    env = os.environ.copy()
    for key in ("GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY",
                "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_CONFIG_PARAMETERS", "GIT_CONFIG_COUNT",
                "GIT_ALLOW_PROTOCOL"):
        env.pop(key, None)
    env.update(GIT_OPTIONAL_LOCKS="0", GIT_TERMINAL_PROMPT="0",
               GIT_NO_LAZY_FETCH="1", GIT_NO_REPLACE_OBJECTS="1")
    command = ["git", "--no-pager", "--no-optional-locks", "-c", "core.fsmonitor=false",
               "-c", "protocol.allow=never", "-C", str(repo), *arguments]
    try:
        result = subprocess.run(command, input=data, stdout=subprocess.PIPE,
                                stderr=subprocess.PIPE, env=env, timeout=60, check=False)
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise PreflightError("Git is unavailable or a read timed out; check was not completed.") from exc
    if result.returncode:
        # Git errors can contain paths, remotes or secrets. Do not echo stderr.
        raise PreflightError(f"Git {arguments[0]} failed (exit {result.returncode}); check was not completed.")
    return result.stdout


def sensitive_path(path: str) -> bool:
    # A template filename is allowed, but its CONTENT is still scanned.
    return Path(path).name != ".env.example" and SENSITIVE.search(path) is not None


def scan(repo: Path, *, max_objects: int = 50000, max_blob_bytes: int = 2_000_000,
         max_total_bytes: int = 64_000_000) -> dict[str, Any]:
    if min(max_objects, max_blob_bytes, max_total_bytes) < 1:
        raise ValueError("Scan budgets must be positive.")
    repo = repo.resolve()
    top = Path(os.fsdecode(git(repo, "rev-parse", "--show-toplevel").strip())).resolve()
    if top != repo:
        raise PreflightError("Use the repository root, not a nested checkout directory.")
    head = git(repo, "rev-parse", "HEAD").decode("ascii").strip()
    if not re.fullmatch(r"[0-9a-f]{40,64}", head):
        raise PreflightError("Unexpected Git object format.")
    findings: list[dict[str, Any]] = []
    counts = {"blocker": 0, "review": 0, "incomplete": 0}

    def add(level: str, code: str, **details: Any) -> None:
        counts[level] += 1
        # Never retain secret values or arbitrary event text in this report.
        if len(findings) < 100:
            findings.append({"severity": level, "code": code, **details})

    shallow = git(repo, "rev-parse", "--is-shallow-repository").strip() == b"true"
    if shallow:
        add("incomplete", "shallow_history")
    if git(repo, "for-each-ref", "--format=%(refname)", "refs/replace").strip():
        add("incomplete", "replacement_refs_present")
    # Config values may contain private remotes; inspect in memory and never
    # put raw configuration into the report. No missing objects are fetched.
    for item in git(repo, "config", "--local", "--null", "--list").split(b"\0"):
        key, _, value = item.partition(b"\n")
        key = key.lower()
        if key == b"extensions.partialclone" or (
            key.startswith(b"remote.") and key.endswith(b".promisor") and
            value.strip().lower() in {b"true", b"1", b"yes", b"on"}
        ):
            add("incomplete", "partial_clone_configuration")
            break
    tracked = {os.fsdecode(p) for p in git(repo, "ls-tree", "-rz", "--name-only", "HEAD").split(b"\0") if p}
    for name in REQUIRED:
        if name not in tracked:
            add("blocker", "required_document_not_committed", document=name)
    if git(repo, "status", "--porcelain=v1", "-z", "--untracked-files=all"):
        add("review", "working_tree_not_clean")
    if any(p.startswith("samples/External/") for p in tracked):
        add("blocker", "external_sample_tracked")
    if any(p.startswith(".github/workflows/") for p in tracked):
        add("blocker", "active_workflow_tracked")
    named = sum(sensitive_path(p) for p in tracked)
    if named:
        add("blocker", "sensitive_current_filenames", count=named)

    # Filename-only history: deleted files must still be inspected before publicizing.
    names = {os.fsdecode(p).lstrip("\n") for p in git(
        repo, "log", "--all", "--format=", "--name-only", "-z", "--no-renames").split(b"\0") if p}
    old_sensitive = sum(sensitive_path(p) for p in names - tracked)
    if old_sensitive:
        add("review", "sensitive_historical_filenames", count=old_sensitive)
    if any(p.startswith(".github/workflows/") for p in names):
        add("review", "historical_workflows_need_review")
    emails = set(git(repo, "log", "--all", "--format=%ae").splitlines())
    personal = sum(bool(e) and not e.lower().endswith(b"@users.noreply.github.com") for e in emails)
    if personal:
        add("review", "non_noreply_author_emails", count=personal)

    # Read every reachable LOCAL blob, including old branches and deleted files.
    # No diff/textconv, no symbol download, and protocol.allow=never prevents a
    # partial-clone object lookup from quietly fetching missing private objects.
    object_ids = sorted(set(git(repo, "rev-list", "--objects", "--all", "--no-object-names").splitlines()))
    if len(object_ids) > max_objects:
        add("incomplete", "object_count_budget", available=len(object_ids), maximum=max_objects)
        object_ids = object_ids[:max_objects]
    if any(not re.fullmatch(rb"[0-9a-f]{40,64}", oid) for oid in object_ids):
        raise PreflightError("Unexpected object identifier; scan stopped.")
    request = b"\n".join(object_ids) + b"\n"
    metadata = git(repo, "cat-file", "--batch-check=%(objectname) %(objecttype) %(objectsize)", data=request)
    selected: list[tuple[bytes, int]] = []
    total = 0
    skipped = 0
    for line in metadata.splitlines():
        fields = line.split()
        if len(fields) != 3 or not fields[2].isdigit():
            raise PreflightError("Missing or malformed Git object; scan incomplete.")
        if fields[1] != b"blob":
            continue
        size = int(fields[2])
        if size > max_blob_bytes or total + size > max_total_bytes:
            skipped += 1
            continue
        total += size
        selected.append((fields[0], size))
    if skipped:
        add("incomplete", "blob_size_or_total_budget", skipped=skipped)

    payload = git(repo, "cat-file", "--batch", data=b"\n".join(oid for oid, _ in selected) + b"\n") if selected else b""
    position = 0
    for oid, size in selected:
        newline = payload.find(b"\n", position)
        expected = oid + b" blob " + str(size).encode("ascii")
        if newline < 0 or payload[position:newline] != expected:
            raise PreflightError("Unexpected batch header; scan incomplete.")
        position = newline + 1
        blob = payload[position:position + size]
        position += size
        if len(blob) != size or payload[position:position + 1] != b"\n":
            raise PreflightError("Truncated blob; scan incomplete.")
        position += 1
        if blob.startswith(b"version https://git-lfs.github.com/spec/v1"):
            add("incomplete", "lfs_payload_not_scanned", object=oid.decode("ascii"))
        for label, pattern in PATTERNS.items():
            if pattern.search(blob):
                add("blocker", "credential_pattern", pattern=label, object=oid.decode("ascii"))
    if position != len(payload):
        raise PreflightError("Unexpected trailing batch output; scan incomplete.")
    if git(repo, "rev-parse", "HEAD").decode("ascii").strip() != head:
        add("incomplete", "head_changed_during_scan")

    exit_code = 2 if counts["blocker"] or counts["incomplete"] else 1 if counts["review"] else 0
    return {
        "schema": 1, "head": head, "scope": "reachable_local_git_refs_only",
        "publicReady": False, "exitCode": exit_code,
        "historyShallow": shallow, "blobObjectsScanned": len(selected),
        "blobBytesScanned": total, "blobObjectsSkipped": skipped,
        "scanCompleteWithinLimits": not counts["incomplete"],
        "counts": counts, "findings": findings,
        "findingsTruncated": sum(counts.values()) > len(findings),
        "manualReviewRequired": [
            "Complete remote refs/tags and changes during the scan are not guaranteed.",
            "Uncommitted/ignored files, submodule contents, and GitHub PR/issue/Actions data are not scanned.",
            "Commit/tag messages, obfuscated or unknown secrets require a dedicated scanner and manual review.",
            "Copyright, trademarks, dependency licenses, vulnerabilities and Windows build/tests remain separate gates.",
            "Exit 0 means no configured heuristic findings, never permission to publish.",
        ],
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--json", action="store_true", help="Machine-readable report; no secret snippets")
    args = parser.parse_args(argv)
    try:
        report = scan(args.repo)
    except (PreflightError, ValueError) as exc:
        report = {"schema": 1, "publicReady": False, "exitCode": 2,
                  "scanCompleteWithinLimits": False, "error": str(exc)}
    if args.json:
        print(json.dumps(report, ensure_ascii=True, indent=2))
    else:
        print("Tenka public-release preflight: LOCAL READ-ONLY HEURISTIC")
        print(f"Exit code: {report['exitCode']} | Public-ready: NO (manual gates remain)")
        if "error" in report:
            print(report["error"])
        else:
            print(f"Blobs scanned: {report['blobObjectsScanned']} | Skipped: {report['blobObjectsSkipped']}")
            for item in report["findings"]:
                print(f"{item['severity'].upper()}: {item['code']}" +
                      (f" [{item['object']}]" if "object" in item else ""))
            print("Review licensing, Windows tests, all remote history and GitHub logs separately.")
    return int(report["exitCode"])


if __name__ == "__main__":
    sys.exit(main())
