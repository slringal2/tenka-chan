"""Test actual Git commands used by the public-readiness PowerShell checker.

Uses only Python standard library and Git in disposable fixture repositories.
This is NOT a PowerShell parser/integration test, a secret scan of Tenka's real
history, or a .NET regression test. It exercises the literal Git argument lists
from check-public-readiness.ps1 so history scans cannot silently ignore merges.
Run: python scripts/test-public-readiness-git.py
"""
from __future__ import annotations

import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile
import unittest

SCRIPT = Path(__file__).with_name("check-public-readiness.ps1")
TEXT = SCRIPT.read_text(encoding="utf-8-sig")
PATTERN = re.search(r"(?m)^\s*\$pattern = '([^']+)'", TEXT).group(1)
GIT = shutil.which("git")


def script_command(name: str) -> list[str]:
    """Read fixed arguments only; do not interpret arbitrary PowerShell code."""
    match = re.search(r"\$" + re.escape(name) + r" = @\(Run-Git @\(((?:'[^']*'|\$pattern|[\s,])*)\)", TEXT)
    if not match:
        raise AssertionError(f"Missing script command for {name}")
    args = []
    for token in re.finditer(r"'([^']*)'|(\$pattern)", match.group(1)):
        args.append(token.group(1) if token.group(1) is not None else PATTERN)
    if not args:
        raise AssertionError(f"No parsed arguments for {name}")
    return args


CURRENT = script_command("currentMatches")
HISTORY = script_command("matchingCommits")
PATHS = script_command("historicPaths")
PARTIAL = script_command("partial")
FAKE = "gh" + "p_" + "A" * 36  # Synthetic; never a credential.


@unittest.skipUnless(GIT, "Git required")
class GitReadinessTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="tenka-readiness-test-")
        self.root = Path(self.temp.name) / "fixture repo 日本語"
        self.root.mkdir()
        self.env = dict(os.environ)
        for name in list(self.env):
            if name.startswith("GIT_"):
                del self.env[name]
        self.env.update(GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=os.devnull,
                        GIT_OPTIONAL_LOCKS="0", GIT_NO_LAZY_FETCH="1",
                        GIT_TERMINAL_PROMPT="0")
        self.git("init", "-q", "-b", "main")
        self.git("config", "user.name", "Fixture")
        self.git("config", "user.email", "fixture@users.noreply.github.com")
        self.write("README.md", "Synthetic fixture, not production data.\n")
        self.commit("initial")

    def tearDown(self) -> None:
        self.temp.cleanup()

    def git(self, *args: str, accepted=(0,), cwd=None) -> str:
        result = subprocess.run(
            [GIT, "--no-pager", "-c", "core.quotePath=false", "-C", str(cwd or self.root), *args],
            capture_output=True, text=True, encoding="utf-8", errors="replace",
            env=self.env, timeout=20,
        )
        if result.returncode not in accepted:
            # Do not include stdout/stderr which could contain matched tokens.
            raise AssertionError(f"git {args[0]} failed: {result.returncode}")
        return result.stdout

    def write(self, name: str, content: str) -> None:
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")

    def commit(self, message: str) -> str:
        self.git("add", "--all")
        self.git("commit", "-qm", message)
        return self.git("rev-parse", "HEAD").strip()

    def hits(self) -> set[str]:
        return set(self.git(*HISTORY).splitlines())

    def test_clean_fixture_has_no_matches(self) -> None:
        self.assertEqual(self.git(*CURRENT, accepted=(0, 1)), "")
        self.assertFalse(self.hits())

    def test_deleting_secret_does_not_erase_historical_match(self) -> None:
        self.write("settings.json", FAKE)
        original = self.commit("synthetic token")
        self.assertEqual(self.git(*CURRENT, accepted=(0, 1)).strip(), "settings.json")
        (self.root / "settings.json").unlink()
        removed = self.commit("remove synthetic token")
        self.assertEqual(self.git(*CURRENT, accepted=(0, 1)), "")
        self.assertTrue({original, removed}.issubset(self.hits()))
        self.assertIn("settings.json", self.git(*PATHS))

    def test_unmerged_branch_is_inspected(self) -> None:
        self.git("checkout", "-qb", "private-old-branch")
        self.write("legacy.txt", FAKE)
        leaked = self.commit("branch fixture")
        self.git("checkout", "-q", "main")
        self.assertEqual(self.git(*CURRENT, accepted=(0, 1)), "")
        self.assertIn(leaked, self.hits())

    def test_tag_only_commit_is_inspected(self) -> None:
        self.git("checkout", "-qb", "temporary")
        self.write("tagged.txt", FAKE)
        leaked = self.commit("tag fixture")
        self.git("tag", "retained-tag")
        self.git("checkout", "-q", "main")
        self.git("branch", "-D", "temporary")
        self.assertIn(leaked, self.hits())

    def test_merge_only_secret_is_not_skipped(self) -> None:
        self.git("checkout", "-qb", "side")
        self.write("side.txt", "ordinary side commit")
        self.commit("side")
        self.git("checkout", "-q", "main")
        self.write("main.txt", "ordinary main commit")
        self.commit("main")
        self.git("merge", "--no-ff", "--no-commit", "side")
        self.write("merge-only.txt", FAKE)
        merged = self.commit("merge-only synthetic token")
        self.assertIn(merged, self.hits())
        # This is the previous bug: default -G log omits merge diffs.
        old = self.git("log", "--all", "--extended-regexp", "-G", PATTERN,
                       "--pretty=format:%H", "--no-patch")
        self.assertNotIn(merged, old.splitlines())

    def test_shallow_clone_is_detectable(self) -> None:
        self.write("two.txt", "second")
        self.commit("second")
        destination = Path(self.temp.name) / "shallow"
        self.git("clone", "--quiet", "--depth", "1", self.root.as_uri(), str(destination))
        self.assertEqual(self.git("rev-parse", "--is-shallow-repository", cwd=destination).strip(), "true")
        self.assertIn("$shallow -ne 'false'", TEXT)

    def test_partial_clone_configuration_is_detected(self) -> None:
        self.assertEqual(self.git(*PARTIAL, accepted=(0, 1)), "")
        self.git("config", "remote.example.promisor", "true")
        self.assertIn("promisor", self.git(*PARTIAL, accepted=(0, 1)))

    def test_external_diff_and_textconv_do_not_run(self) -> None:
        marker = self.root / "helper-was-run"
        helper = self.root / "helper.py"
        helper.write_text("from pathlib import Path\nPath(" + repr(str(marker)) + ").write_text('bad')\n", encoding="utf-8")
        command = '"' + sys.executable.replace('\\', '/') + '" "' + str(helper).replace('\\', '/') + '"'
        self.git("config", "diff.audit.command", command)
        self.git("config", "diff.audit.textconv", command)
        self.write(".gitattributes", "*.txt diff=audit\n")
        self.write("input.txt", "old")
        self.commit("driver config fixture")
        self.write("input.txt", FAKE)
        self.commit("driver token fixture")
        self.assertTrue(self.hits())
        self.git(*PATHS)
        self.assertFalse(marker.exists())

    def test_scan_preserves_git_index_and_refs(self) -> None:
        head = self.git("rev-parse", "HEAD")
        index = (self.root / ".git" / "index").read_bytes()
        config = (self.root / ".git" / "config").read_bytes()
        self.git(*CURRENT, accepted=(0, 1))
        self.hits()
        self.git(*PATHS)
        self.assertEqual(head, self.git("rev-parse", "HEAD"))
        self.assertEqual(index, (self.root / ".git" / "index").read_bytes())
        self.assertEqual(config, (self.root / ".git" / "config").read_bytes())

    def test_commands_hide_matches_and_include_merge_diffs(self) -> None:
        self.assertIn("-l", CURRENT)
        self.assertIn("--no-patch", HISTORY)
        self.assertIn("-m", HISTORY)
        self.assertIn("--no-ext-diff", HISTORY)
        self.assertIn("--no-textconv", HISTORY)
        for args in (CURRENT, HISTORY, PATHS, PARTIAL):
            self.assertNotIn(args[0], {"push", "fetch", "reset", "clean", "filter-branch"})

    def test_completeness_guards_precede_content_scan(self) -> None:
        self.assertLess(TEXT.index("--is-shallow-repository"), TEXT.index("$currentMatches ="))
        self.assertLess(TEXT.index("--missing=error"), TEXT.index("$currentMatches ="))
        self.assertIn("finally {", TEXT)
        self.assertIn("$env:GIT_OPTIONAL_LOCKS = $oldLocks", TEXT)
        self.assertIn("$env:GIT_NO_LAZY_FETCH = $oldLazy", TEXT)
        self.assertEqual(SCRIPT.read_bytes()[:3], b"\xef\xbb\xbf")


if __name__ == "__main__":
    unittest.main(verbosity=2)
