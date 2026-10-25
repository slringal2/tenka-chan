"""Standard-library tests using throwaway local Git repositories. No network."""
from __future__ import annotations

import contextlib
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

HERE = Path(__file__).resolve()
SPEC = importlib.util.spec_from_file_location(
    "public_release_preflight", HERE.parents[1] / "scripts/public_release_preflight.py")
assert SPEC is not None and SPEC.loader is not None
preflight = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(preflight)


class PublicReleasePreflightTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="tenka-public-test-")
        self.root = Path(self.temp.name)
        self.repo = self.root / "repo"
        self.repo.mkdir()
        self.run_git("init", "-q", "-b", "main")
        self.run_git("config", "user.name", "Preflight Test")
        self.run_git("config", "user.email", "fixture@users.noreply.github.com")
        self.run_git("config", "commit.gpgsign", "false")
        for name in preflight.REQUIRED:
            self.write(name, "Fixture documentation, not legal approval.\n")
        self.commit("fixture root")

    def tearDown(self) -> None:
        self.temp.cleanup()

    def run_git(self, *args: str) -> bytes:
        result = subprocess.run(["git", "-C", str(self.repo), *args], check=True,
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                env={**os.environ, "GIT_TERMINAL_PROMPT": "0"})
        return result.stdout

    def write(self, path: str, text: str) -> None:
        file = self.repo / path
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_text(text, encoding="utf-8")

    def commit(self, message: str) -> None:
        self.run_git("add", ".")
        self.run_git("commit", "-qm", message)

    @staticmethod
    def codes(report: dict) -> set[str]:
        return {x["code"] for x in report["findings"]}

    def test_clean_fixture_is_not_a_publication_approval(self) -> None:
        result = preflight.scan(self.repo)
        self.assertEqual(0, result["exitCode"])
        self.assertTrue(result["scanCompleteWithinLimits"])
        self.assertFalse(result["publicReady"])
        self.assertGreater(result["blobObjectsScanned"], 0)
        self.assertTrue(result["manualReviewRequired"])

    def test_deleted_secret_is_found_without_echoing_value(self) -> None:
        secret = "gh" + "p_" + "A" * 36
        self.write("notes.txt", "token = " + secret)
        self.commit("add fixture data")
        self.run_git("rm", "notes.txt")
        self.commit("remove fixture data")
        result = preflight.scan(self.repo)
        self.assertEqual(2, result["exitCode"])
        self.assertIn("credential_pattern", self.codes(result))
        self.assertNotIn(secret, json.dumps(result))
        self.assertFalse((self.repo / "notes.txt").exists())

    def test_secret_on_another_local_branch_is_found(self) -> None:
        self.run_git("checkout", "-qb", "hidden-feature")
        self.write("hidden.txt", "github_" + "pat_" + "Z" * 48)
        self.commit("hidden fixture")
        self.run_git("checkout", "-q", "main")
        result = preflight.scan(self.repo)
        self.assertIn("credential_pattern", self.codes(result))
        self.assertFalse((self.repo / "hidden.txt").exists())

    def test_env_example_name_is_allowed_but_its_content_is_scanned(self) -> None:
        self.write(".env.example", "URL=http://localhost\n")
        self.commit("safe template")
        result = preflight.scan(self.repo)
        self.assertNotIn("sensitive_current_filenames", self.codes(result))
        self.write(".env.example", "KEY=" + "sk-" + "b" * 40)
        self.commit("unsafe fixture template")
        result = preflight.scan(self.repo)
        self.assertIn("credential_pattern", self.codes(result))

    def test_deleted_sensitive_filename_is_still_reported(self) -> None:
        self.write(".env", "ordinary fixture content")
        self.commit("local file fixture")
        self.run_git("rm", ".env")
        self.commit("remove local file fixture")
        result = preflight.scan(self.repo)
        self.assertIn("sensitive_historical_filenames", self.codes(result))
        self.assertEqual(1, result["exitCode"])

    def test_shallow_clone_cannot_be_marked_complete(self) -> None:
        self.write("change.txt", "second commit")
        self.commit("second")
        clone = self.root / "shallow"
        subprocess.run(["git", "clone", "-q", "--depth=1", self.repo.as_uri(), str(clone)],
                       check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        result = preflight.scan(clone)
        self.assertTrue(result["historyShallow"])
        self.assertFalse(result["scanCompleteWithinLimits"])
        self.assertEqual(2, result["exitCode"])

    def test_partial_clone_configuration_is_incomplete(self) -> None:
        self.run_git("config", "remote.fixture.promisor", "true")
        result = preflight.scan(self.repo)
        self.assertIn("partial_clone_configuration", self.codes(result))
        self.assertFalse(result["scanCompleteWithinLimits"])
        self.assertEqual(2, result["exitCode"])

    def test_replacement_refs_cannot_hide_original_secret(self) -> None:
        original = self.run_git("rev-parse", "HEAD").decode("ascii").strip()
        self.write("secret.txt", "gh" + "p_" + "X" * 36)
        self.commit("fixture secret")
        bad = self.run_git("rev-parse", "HEAD").decode("ascii").strip()
        self.run_git("replace", bad, original)
        result = preflight.scan(self.repo)
        self.assertIn("replacement_refs_present", self.codes(result))
        self.assertIn("credential_pattern", self.codes(result))
        self.assertFalse(result["scanCompleteWithinLimits"])

    def test_oversize_blob_is_incomplete_not_clean(self) -> None:
        self.write("large.txt", "x" * 1000)
        self.commit("large fixture")
        result = preflight.scan(self.repo, max_blob_bytes=100)
        self.assertGreater(result["blobObjectsSkipped"], 0)
        self.assertFalse(result["scanCompleteWithinLimits"])
        self.assertEqual(2, result["exitCode"])

    def test_object_count_budget_is_incomplete_not_clean(self) -> None:
        result = preflight.scan(self.repo, max_objects=1)
        self.assertIn("object_count_budget", self.codes(result))
        self.assertFalse(result["scanCompleteWithinLimits"])

    def test_total_byte_budget_is_incomplete_not_clean(self) -> None:
        result = preflight.scan(self.repo, max_total_bytes=1)
        self.assertGreater(result["blobObjectsSkipped"], 0)
        self.assertFalse(result["scanCompleteWithinLimits"])

    def test_lfs_payload_is_not_claimed_to_have_been_scanned(self) -> None:
        self.write("large-lfs.txt", "version https://git-lfs.github.com/spec/v1\n"
                   + "oid sha256:" + "a" * 64 + "\nsize 3000000\n")
        self.commit("LFS pointer fixture")
        result = preflight.scan(self.repo)
        self.assertIn("lfs_payload_not_scanned", self.codes(result))
        self.assertFalse(result["scanCompleteWithinLimits"])

    def test_actions_and_external_sample_paths_are_blockers(self) -> None:
        self.write(".github/workflows/ci.yml", "name: fixture\n")
        self.write("samples/External/fixture.cs", "class C {}")
        self.commit("restricted path fixture")
        result = preflight.scan(self.repo)
        self.assertIn("active_workflow_tracked", self.codes(result))
        self.assertIn("external_sample_tracked", self.codes(result))
        self.assertEqual(2, result["exitCode"])

    def test_missing_document_must_be_committed_not_just_on_disk(self) -> None:
        self.run_git("rm", "SECURITY.md")
        self.commit("remove security fixture")
        self.write("SECURITY.md", "uncommitted file")
        result = preflight.scan(self.repo)
        self.assertIn("required_document_not_committed", self.codes(result))

    def test_scan_does_not_mutate_index_refs_or_working_tree(self) -> None:
        index = self.repo / ".git/index"
        before_index = hashlib.sha256(index.read_bytes()).digest()
        before_refs = self.run_git("show-ref")
        before_status = self.run_git("status", "--porcelain", "--untracked-files=all")
        preflight.scan(self.repo)
        self.assertEqual(before_index, hashlib.sha256(index.read_bytes()).digest())
        self.assertEqual(before_refs, self.run_git("show-ref"))
        self.assertEqual(before_status, self.run_git("status", "--porcelain", "--untracked-files=all"))

    def test_json_output_does_not_echo_secret_or_personal_email(self) -> None:
        secret = "AKIA" + "C" * 16
        email = "private-fixture@example.invalid"
        self.run_git("config", "user.email", email)
        self.write("notes.txt", secret)
        self.commit("credential fixture")
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            code = preflight.main(["--repo", str(self.repo), "--json"])
        self.assertEqual(2, code)
        self.assertNotIn(secret, output.getvalue())
        self.assertNotIn(email, output.getvalue())
        self.assertFalse(json.loads(output.getvalue())["publicReady"])

    def test_unavailable_repository_returns_failure_not_empty_success(self) -> None:
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            code = preflight.main(["--repo", str(self.root / "missing"), "--json"])
        self.assertEqual(2, code)
        self.assertFalse(json.loads(output.getvalue())["scanCompleteWithinLimits"])


if __name__ == "__main__":
    unittest.main()
