"""Execute the offline license collector on disposable, synthetic NuGet caches."""
from __future__ import annotations

import contextlib
import hashlib
import importlib.util
import io
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts" / "collect-package-licenses.py"
SPEC = importlib.util.spec_from_file_location("package_inventory", SCRIPT)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class PackageLicenseInventoryTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="tenka-license-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.cache = self.root / "packages"
        self.cache.mkdir()
        self.asset = self.root / "project.assets.json"
        self.graph = {"version": 3, "targets": {"net10.0": {}}, "libraries": {},
                      "packageFolders": {str(self.root / "do-not-trust-this-path"): {}}}

    def package(self, name="Fixture.Core", version="1.2.3", license_xml='<license type="expression">MIT</license>',
                files=None, namespace=False) -> Path:
        key = name + "/" + version
        path = name.lower() + "/" + MODULE.normalized_version(version)
        folder = self.cache / path
        folder.mkdir(parents=True, exist_ok=True)
        files = dict(files or {})
        ns = ' xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"' if namespace else ""
        metadata = (f'<package{ns}><metadata><id>{name}</id><version>{version}</version>'
                    f'{license_xml}</metadata></package>')
        (folder / (name.lower() + ".nuspec")).write_text(metadata, encoding="utf-8")
        for relative, content in files.items():
            target = folder / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(content, encoding="utf-8")
        self.graph["targets"]["net10.0"][key] = {"type": "package"}
        self.graph["libraries"][key] = {"type": "package", "path": path,
                                            "files": [name.lower() + ".nuspec", *files]}
        self.save()
        return folder

    def save(self) -> None:
        self.asset.write_text(json.dumps(self.graph), encoding="utf-8")

    def report(self):
        return MODULE.collect([self.asset], [self.cache])

    def test_direct_and_transitive_records_are_both_collected_without_guessing_legal_clearance(self):
        self.package()
        self.package("Fixture.Transitive", "2.0.0", '<license type="expression">Apache-2.0</license>')
        report = self.report()
        self.assertTrue(report["inventoryComplete"])
        self.assertEqual(2, report["packageCount"])
        self.assertEqual(["MIT", "Apache-2.0"], [x["licenseExpression"] for x in report["packages"]])
        self.assertFalse(report["publicReady"])
        self.assertFalse(report["licenseCompatibilityVerified"])
        self.assertFalse(report["cacheAuthenticityVerified"])

    def test_namespaced_nuspec_file_license_and_notices_are_hashed_not_copied(self):
        self.package(license_xml='<license type="file">legal/LICENSE.txt</license>', namespace=True,
                     files={"legal/LICENSE.txt": "LICENSE-CONTENT-MUST-NOT-LEAK",
                            "ThirdPartyNotices.txt": "NOTICE-CONTENT-MUST-NOT-LEAK"})
        report = self.report()
        self.assertTrue(report["inventoryComplete"])
        row = report["packages"][0]
        self.assertEqual("file", row["licenseKind"])
        self.assertEqual(2, len(row["evidenceFiles"]))
        license_file = next(x for x in row["evidenceFiles"] if x["declaredLicense"])
        self.assertEqual(hashlib.sha256(b"LICENSE-CONTENT-MUST-NOT-LEAK").hexdigest(), license_file["sha256"])
        self.assertNotIn("CONTENT-MUST-NOT-LEAK", json.dumps(report))

    def test_missing_declared_license_is_incomplete(self):
        self.package(license_xml='<license type="file">LICENSE.txt</license>')
        report = self.report()
        self.assertFalse(report["inventoryComplete"])
        self.assertIn("evidence_local_input_unavailable", report["packages"][0]["issues"])

    def test_legacy_license_url_is_not_fetched_or_printed(self):
        secret_url = "https://token:private-secret@example.invalid/license"
        self.package(license_xml='<licenseUrl>' + secret_url + '</licenseUrl>')
        report = self.report()
        self.assertFalse(report["inventoryComplete"])
        self.assertEqual("legacy_url", report["packages"][0]["licenseKind"])
        self.assertNotIn(secret_url, json.dumps(report))
        self.assertNotIn("private-secret", json.dumps(report))

    def test_missing_license_is_not_inferred_from_parent_project_license(self):
        self.package(license_xml="")
        row = self.report()["packages"][0]
        self.assertEqual("missing", row["licenseKind"])
        self.assertIsNone(row["licenseExpression"])
        self.assertIn("license_metadata_missing", row["issues"])

    def test_nuspec_identity_mismatch_fails_closed(self):
        folder = self.package()
        path = folder / "fixture.core.nuspec"
        path.write_text(path.read_text().replace("Fixture.Core", "Different.Package"))
        report = self.report()
        self.assertFalse(report["inventoryComplete"])
        self.assertIn("nuspec_identity_mismatch", report["packages"][0]["issues"])

    def test_library_path_cannot_read_another_package(self):
        self.package()
        self.graph["libraries"]["Fixture.Core/1.2.3"]["path"] = "other.package/1.2.3"
        self.save()
        report = self.report()
        self.assertFalse(report["inventoryComplete"])
        self.assertIn("package_path_identity_mismatch", report["packages"][0]["issues"])

    def test_assets_package_folders_are_never_used_as_implicit_allowlist(self):
        self.package()
        empty = self.root / "explicit-empty-cache"
        empty.mkdir()
        self.graph["packageFolders"] = {str(self.cache): {}}
        self.save()
        report = MODULE.collect([self.asset], [empty])
        self.assertFalse(report["inventoryComplete"])
        self.assertIn("package_cache_missing", report["packages"][0]["issues"])
        self.assertNotIn(str(self.cache), json.dumps(report))

    def test_path_traversal_absolute_and_ads_license_files_are_rejected(self):
        for path in ("../secret", "/etc/passwd", "C:/private/key", r"..\secret", "legal/license:secret"):
            with self.subTest(path=path):
                self.package(license_xml=f'<license type="file">{path}</license>')
                report = self.report()
                self.assertFalse(report["inventoryComplete"])
                self.assertIn("unsafe_package_relative_path", report["packages"][0]["issues"])

    def test_symlink_license_file_is_not_read(self):
        folder = self.package(license_xml='<license type="file">LICENSE.txt</license>')
        secret = self.root / "not-allowed"
        secret.write_text("MUST-NOT-BE-READ")
        try:
            (folder / "LICENSE.txt").symlink_to(secret)
        except OSError:
            self.skipTest("OS does not permit symlink fixture")
        report = self.report()
        self.assertFalse(report["inventoryComplete"])
        self.assertIn("evidence_linked_path_rejected", report["packages"][0]["issues"])
        self.assertNotIn(hashlib.sha256(b"MUST-NOT-BE-READ").hexdigest(), json.dumps(report))

    def test_symlink_package_root_is_rejected(self):
        self.package()
        link = self.root / "linked-cache"
        try:
            link.symlink_to(self.cache, target_is_directory=True)
        except OSError:
            self.skipTest("OS does not permit symlink fixture")
        with self.assertRaisesRegex(MODULE.InventoryError, "linked_path_rejected"):
            MODULE.collect([self.asset], [link])

    def test_dtd_entity_and_utf16_xml_are_rejected(self):
        folder = self.package()
        path = folder / "fixture.core.nuspec"
        for data in (b'<!DOCTYPE package [<!ENTITY x SYSTEM "file:///etc/passwd">]><package/>',
                     '<package/>'.encode('utf-16')):
            with self.subTest(data=data[:20]):
                path.write_bytes(data)
                report = self.report()
                self.assertFalse(report["inventoryComplete"])
                self.assertFalse(report["packages"][0]["evidenceFiles"])

    def test_duplicate_json_keys_do_not_hide_package_records(self):
        self.asset.write_text('{"version":3,"libraries":{},"libraries":{},"targets":{"net10.0":{}}}')
        report = self.report()
        self.assertFalse(report["inventoryComplete"])
        self.assertIn("duplicate_json_key", report["assets"][0]["issues"])

    def test_missing_invalid_or_unsupported_assets_are_incomplete_not_empty_success(self):
        for content in (None, "not json", '{"version":99,"targets":{},"libraries":{}}', '[]'):
            with self.subTest(content=content):
                if content is None:
                    self.asset.unlink(missing_ok=True)
                else:
                    self.asset.write_text(content)
                report = self.report()
                self.assertFalse(report["inventoryComplete"])
                self.assertFalse(report["publicReady"])

    def test_multiple_assets_deduplicate_same_identity_but_retain_input_provenance(self):
        self.package()
        second = self.root / "second.assets.json"
        second.write_bytes(self.asset.read_bytes())
        report = MODULE.collect([self.asset, second], [self.cache])
        self.assertTrue(report["inventoryComplete"])
        self.assertEqual(1, report["packageCount"])
        self.assertEqual([1, 2], report["packages"][0]["assetInputs"])
        self.assertEqual(2, len(report["assets"]))

    def test_conflicting_assets_metadata_is_visible(self):
        self.package()
        second = self.root / "second.assets.json"
        self.graph["libraries"]["Fixture.Core/1.2.3"]["files"].append("NEW-NOTICE.txt")
        second.write_text(json.dumps(self.graph))
        report = MODULE.collect([self.asset, second], [self.cache])
        self.assertFalse(report["inventoryComplete"])
        self.assertIn("conflicting_assets_package_metadata", report["packages"][0]["issues"])

    def test_multiple_caches_do_not_silently_choose_one_package(self):
        self.package()
        import shutil
        second = self.root / "other-cache"
        shutil.copytree(self.cache, second)
        report = MODULE.collect([self.asset], [self.cache, second])
        self.assertFalse(report["inventoryComplete"])
        self.assertIn("ambiguous_package_roots", report["packages"][0]["issues"])

    def test_normalized_nuget_version_matches_nuspec_short_version(self):
        folder = self.package(version="1.0.0")
        path = folder / "fixture.core.nuspec"
        path.write_text(path.read_text().replace("<version>1.0.0</version>", "<version>1.0</version>"))
        self.assertTrue(self.report()["inventoryComplete"])
        self.assertEqual("1.2.3-preview.1", MODULE.normalized_version("1.2.3.0-PREVIEW.1+build"))

    def test_project_references_are_not_relicensed_or_treated_as_nuget(self):
        self.graph["libraries"]["OtherProject/1.0.0"] = {"type": "project", "path": "../OtherProject.csproj"}
        self.graph["targets"]["net10.0"]["OtherProject/1.0.0"] = {"type": "project"}
        self.save()
        report = self.report()
        self.assertTrue(report["inventoryComplete"])
        self.assertEqual(0, report["packageCount"])
        self.assertFalse(report["publicReady"])

    def test_unknown_target_reference_marks_partial_coverage(self):
        self.package()
        self.graph["targets"]["net10.0"]["Missing.Dependency/1.0.0"] = {"type": "package"}
        self.save()
        report = self.report()
        self.assertFalse(report["inventoryComplete"])
        self.assertIn("target_library_not_declared", report["assets"][0]["issues"])

    def test_notice_file_and_total_budget_limits_are_reported(self):
        self.package(files={"NOTICE.txt": "n" * 300})
        with mock.patch.object(MODULE, "MAX_NOTICE_BYTES", 64):
            report = self.report()
        self.assertIn("evidence_file_size_limit", report["packages"][0]["issues"])
        with mock.patch.object(MODULE, "MAX_TOTAL_BYTES", 8):
            report = self.report()
        self.assertFalse(report["inventoryComplete"])
        self.assertIn("total_input_limit", report["assets"][0]["issues"])

    def test_package_count_limit_cannot_be_reported_complete(self):
        self.package()
        self.package("Fixture.Other")
        with mock.patch.object(MODULE, "MAX_PACKAGES", 1):
            report = self.report()
        self.assertFalse(report["inventoryComplete"])
        self.assertEqual(1, report["packageCount"])
        self.assertIn("package_count_limit", report["assets"][0]["issues"])

    def test_network_and_process_execution_are_not_used(self):
        self.package()
        with mock.patch("socket.create_connection", side_effect=AssertionError("network forbidden")), \
             mock.patch("subprocess.Popen", side_effect=AssertionError("process forbidden")):
            report = self.report()
        self.assertTrue(report["inventoryComplete"])

    def test_report_excludes_local_paths_and_is_repeatable_without_modifying_inputs(self):
        folder = self.package(files={"LICENSE.txt": "plain license"})
        before = {p: p.read_bytes() for p in self.root.rglob("*") if p.is_file()}
        report = self.report()
        self.assertNotIn(str(self.root), json.dumps(report))
        self.assertEqual(report, self.report())
        after = {p: p.read_bytes() for p in self.root.rglob("*") if p.is_file()}
        self.assertEqual(before, after)
        self.assertTrue(folder.is_dir())

    def test_cli_complete_output_and_refusal_to_overwrite(self):
        self.package()
        output = self.root / "licenses.json"
        command = [sys.executable, str(SCRIPT), "--assets", str(self.asset),
                   "--package-root", str(self.cache), "--output", str(output)]
        result = subprocess.run(command, text=True, capture_output=True, timeout=10)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertTrue(json.loads(output.read_text())["inventoryComplete"])
        self.assertFalse(json.loads(output.read_text())["publicReady"])
        old = output.read_bytes()
        repeat = subprocess.run(command, text=True, capture_output=True, timeout=10)
        self.assertEqual(2, repeat.returncode)
        self.assertEqual(old, output.read_bytes())
        self.assertNotIn(str(self.root), repeat.stderr)

    def test_cli_missing_metadata_returns_one_with_partial_json_not_zero(self):
        self.package(license_xml="")
        result = subprocess.run([sys.executable, str(SCRIPT), "--assets", str(self.asset),
                                 "--package-root", str(self.cache)],
                                text=True, capture_output=True, timeout=10)
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertFalse(json.loads(result.stdout)["inventoryComplete"])
        self.assertNotIn(str(self.root), result.stdout)

    def test_expression_urls_and_control_chars_are_not_emitted(self):
        self.package(license_xml='<license type="expression">https://secret:password@example.invalid</license>')
        report = self.report()
        self.assertFalse(report["inventoryComplete"])
        self.assertIsNone(report["packages"][0]["licenseExpression"])
        self.assertNotIn("password", json.dumps(report))


if __name__ == "__main__":
    unittest.main(verbosity=2)
