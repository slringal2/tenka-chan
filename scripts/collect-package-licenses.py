#!/usr/bin/env python3
"""Offline inventory of declared NuGet licenses; NOT legal/publication approval.

Read explicitly supplied project.assets.json files and explicitly allowed package
roots only. Never restore/build, invoke a process, fetch URLs, load assemblies,
copy license text or change repository state. Python is optional release tooling.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import stat
import sys
import xml.etree.ElementTree as ET
from pathlib import Path, PurePosixPath, PureWindowsPath
from typing import Any

MAX_ASSET_BYTES = 16_000_000
MAX_METADATA_BYTES = 1_000_000
MAX_NOTICE_BYTES = 4_000_000
MAX_TOTAL_BYTES = 64_000_000
MAX_PACKAGES = 4000
MAX_PACKAGE_FILES = 10000
MAX_NOTICE_FILES = 64
ID = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,99}\Z")
VERSION = re.compile(r"(\d+(?:\.\d+){0,3})(-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?\Z")
NOTICE = re.compile(r"^(?:(?:licen[cs]e|copying|notice)(?:[._ -].*)?|third[._ -]?party(?:[._ -]?notices?)?(?:[._ -].*)?)$", re.I)


class InventoryError(ValueError):
    """Codes only: exception text must not expose a machine path or credential."""


def ordinary(path: Path, directory: bool = False) -> Path:
    """Reject symlinks/junctions, including ancestors; not a hostile-OS sandbox."""
    path = Path(os.path.abspath(path))
    try:
        for item in (*reversed(path.parents), path):
            attrs = item.lstat()
            if stat.S_ISLNK(attrs.st_mode) or getattr(attrs, "st_file_attributes", 0) & 0x400:
                raise InventoryError("linked_path_rejected")
        mode = path.lstat().st_mode
        if not (stat.S_ISDIR(mode) if directory else stat.S_ISREG(mode)):
            raise InventoryError("not_a_regular_directory" if directory else "not_a_regular_file")
    except OSError as exc:
        raise InventoryError("local_input_unavailable") from exc
    return path


def relative(value: Any) -> str:
    if not isinstance(value, str) or not value or len(value) > 1024:
        raise InventoryError("invalid_package_relative_path")
    value = value.replace("\\", "/")
    if (any(ord(c) < 32 or ord(c) == 127 for c in value) or ":" in value
            or PureWindowsPath(value).drive or PurePosixPath(value).is_absolute()
            or any(p in ("", ".", "..") for p in value.split("/"))):
        raise InventoryError("unsafe_package_relative_path")
    return value


def normalized_version(version: str) -> str:
    match = VERSION.fullmatch(version)
    if not match:
        raise InventoryError("invalid_resolved_package_version")
    parts = [int(p) for p in match[1].split(".")]
    while len(parts) < 3:
        parts.append(0)
    if len(parts) == 4 and parts[-1] == 0:
        parts.pop()
    return ".".join(map(str, parts)) + (match[2] or "").lower()


def identity(key: str) -> tuple[str, str]:
    if not isinstance(key, str) or key.count("/") != 1:
        raise InventoryError("invalid_resolved_package_identity")
    name, version = key.split("/")
    if not ID.fullmatch(name):
        raise InventoryError("invalid_resolved_package_identity")
    return name, normalized_version(version)


class Budget:
    def __init__(self) -> None:
        self.read = 0

    def load(self, path: Path, maximum: int) -> bytes:
        path = ordinary(path)
        try:
            with path.open("rb") as stream:
                size = os.fstat(stream.fileno()).st_size
                if size > maximum:
                    raise InventoryError("file_size_limit")
                if self.read + size > MAX_TOTAL_BYTES:
                    raise InventoryError("total_input_limit")
                data = stream.read(min(maximum, MAX_TOTAL_BYTES - self.read) + 1)
        except OSError as exc:
            raise InventoryError("local_input_unavailable") from exc
        self.read += len(data)
        if len(data) > maximum or self.read > MAX_TOTAL_BYTES:
            raise InventoryError("input_limit_during_read")
        return data


def digest(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise InventoryError("duplicate_json_key")
        result[key] = value
    return result


def plain_text(value: str | None, maximum: int = 500) -> str:
    value = (value or "").strip()
    if not value or len(value) > maximum or any(ord(c) < 32 or ord(c) == 127 for c in value):
        raise InventoryError("invalid_license_metadata")
    return value


def package_evidence(key: str, library: dict[str, Any], roots: list[Path], budget: Budget) -> dict[str, Any]:
    name, version = identity(key)
    expected_path = name.lower() + "/" + version
    row: dict[str, Any] = {"id": name, "version": version, "licenseKind": "missing",
                           "licenseExpression": None, "evidenceFiles": [], "issues": []}
    try:
        package_path = relative(library.get("path"))
        if package_path.lower() != expected_path:
            raise InventoryError("package_path_identity_mismatch")
        # Do not trust assets.packageFolders: only caller-provided roots are used.
        candidates = [root / package_path for root in roots if os.path.lexists(root / package_path)]
        if len(candidates) != 1:
            raise InventoryError("package_cache_missing" if not candidates else "ambiguous_package_roots")
        package = ordinary(candidates[0], directory=True)
        files = library.get("files")
        if not isinstance(files, list) or len(files) > MAX_PACKAGE_FILES:
            raise InventoryError("package_files_list_missing_or_over_limit")
        files = sorted({relative(p) for p in files})
        metadata_path = package / (name.lower() + ".nuspec")
        raw = budget.load(metadata_path, MAX_METADATA_BYTES)
        # Reject UTF-16/NUL and DTD/entity declarations before the XML parser.
        text = raw.decode("utf-8-sig")
        if "\x00" in text or re.search(r"<!\s*(?:DOCTYPE|ENTITY)", text, re.I):
            raise InventoryError("xml_dtd_or_entity_rejected")
        xml = ET.fromstring(text)
        local = lambda tag: tag.rsplit("}", 1)[-1]
        if local(xml.tag) != "package":
            raise InventoryError("invalid_nuspec_root")
        nodes = [node for node in xml if local(node.tag) == "metadata"]
        if len(nodes) != 1:
            raise InventoryError("invalid_nuspec_metadata")
        fields: dict[str, ET.Element] = {}
        for node in nodes[0]:
            field = local(node.tag)
            if field in fields:
                raise InventoryError("duplicate_nuspec_metadata")
            fields[field] = node
        actual_id = plain_text(fields["id"].text, 100)
        actual_version = normalized_version(plain_text(fields["version"].text))
        if actual_id.lower() != name.lower() or actual_version != version:
            raise InventoryError("nuspec_identity_mismatch")
        row["nuspecSha256"] = digest(raw)
        artifact_paths = {p for p in files if NOTICE.match(PurePosixPath(p).name)}
        license_node = fields.get("license")
        declared_file: str | None = None
        if license_node is not None:
            kind = license_node.attrib.get("type")
            value = plain_text(license_node.text)
            if kind == "expression":
                if not re.fullmatch(r"[A-Za-z0-9().+ -]+", value):
                    raise InventoryError("invalid_license_expression_characters")
                row.update(licenseKind="expression", licenseExpression=value)
            elif kind == "file":
                declared_file = relative(value)
                row.update(licenseKind="file", licenseFile=declared_file)
                artifact_paths.add(declared_file)
            else:
                raise InventoryError("unsupported_license_metadata_type")
        elif "licenseUrl" in fields:
            row["licenseKind"] = "legacy_url"
            row["issues"].append("license_url_only_not_fetched")
        else:
            row["issues"].append("license_metadata_missing")
        if len(artifact_paths) > MAX_NOTICE_FILES:
            raise InventoryError("notice_file_count_limit")
        for path in sorted(artifact_paths):
            try:
                data = budget.load(package / path, MAX_NOTICE_BYTES)
                row["evidenceFiles"].append({"path": path, "sha256": digest(data), "bytes": len(data),
                                             "declaredLicense": path == declared_file})
            except InventoryError as exc:
                row["issues"].append("evidence_" + str(exc))
    except (InventoryError, KeyError, ET.ParseError, UnicodeError) as exc:
        row["issues"].append(str(exc) if isinstance(exc, InventoryError) else "malformed_nuspec")
    row["issues"] = sorted(set(row["issues"]))
    return row


def collect(assets: list[Path], package_roots: list[Path]) -> dict[str, Any]:
    if not 1 <= len(assets) <= 32 or not 1 <= len(package_roots) <= 8:
        raise InventoryError("asset_or_package_root_count_limit")
    roots = list(dict.fromkeys(ordinary(p, directory=True) for p in package_roots))
    budget = Budget()
    rows: dict[tuple[str, str], dict[str, Any]] = {}
    documents: list[dict[str, Any]] = []
    issues: list[str] = []
    declarations: dict[tuple[str, str], tuple[Any, Any]] = {}
    for number, path in enumerate(assets, 1):
        document: dict[str, Any] = {"input": number, "issues": []}
        documents.append(document)
        try:
            raw = budget.load(path, MAX_ASSET_BYTES)
            graph = json.loads(raw.decode("utf-8-sig"), object_pairs_hook=unique_object)
            document["sha256"] = digest(raw)
            if (not isinstance(graph, dict) or graph.get("version") != 3
                    or not isinstance(graph.get("libraries"), dict)
                    or not isinstance(graph.get("targets"), dict) or not graph["targets"]):
                raise InventoryError("unsupported_assets_shape")
            libraries = graph["libraries"]
            for target in graph["targets"].values():
                if not isinstance(target, dict):
                    raise InventoryError("invalid_target_graph")
                for key in target:
                    if key not in libraries:
                        document["issues"].append("target_library_not_declared")
            # Conservative scope: all package libraries, not only shipped runtime assets.
            for key, library in libraries.items():
                if not isinstance(library, dict) or library.get("type") not in ("package", "project"):
                    document["issues"].append("unknown_library_type")
                    continue
                if library["type"] == "project":
                    continue
                name, version = identity(key)
                canonical = (name.lower(), version)
                signature = (library.get("path"), library.get("files"))
                if canonical in rows:
                    if declarations[canonical] != signature:
                        rows[canonical]["issues"].append("conflicting_assets_package_metadata")
                    rows[canonical]["assetInputs"].append(number)
                    continue
                if len(rows) >= MAX_PACKAGES:
                    raise InventoryError("package_count_limit")
                row = package_evidence(key, library, roots, budget)
                row["assetInputs"] = [number]
                rows[canonical] = row
                declarations[canonical] = signature
        except (InventoryError, UnicodeError, ValueError, RecursionError) as exc:
            document["issues"].append(str(exc) if isinstance(exc, InventoryError) else "invalid_assets_json")
        document["issues"] = sorted(set(document["issues"]))
        issues.extend(document["issues"])
    packages = [rows[k] for k in sorted(rows)]
    complete = not issues and all(not p["issues"] for p in packages)
    return {
        "schemaVersion": 1,
        "status": "inventory_complete_manual_review_required" if complete else "inventory_incomplete",
        "inventoryComplete": complete, "publicReady": False, "licenseCompatibilityVerified": False,
        "cacheAuthenticityVerified": False, "packageCount": len(packages), "bytesRead": budget.read,
        "assets": documents, "packages": packages,
        "scope": "All package libraries recorded in the explicitly supplied assets files; includes direct/transitive, build and test dependencies. Not a shipped-binary SBOM.",
        "limitations": [
            "No restore, build, network fetch, symbol download or target execution occurred.",
            "Assets may be stale; input hashes record the observed files, not build/source identity.",
            "Expressions are package-declared metadata, not validated SPDX or a legal compatibility verdict.",
            "Evidence files are hashed, not copied or legally reviewed; retaining notices is a separate distribution step.",
            "Notice discovery uses recorded filenames, not a complete content/embedded-binary audit.",
            "Cache files are not authenticated against signed packages; hashes alone do not establish provenance.",
            "No local absolute paths, URLs or license text are included; review the report before sharing it.",
        ],
    }


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets", type=Path, required=True, action="append")
    parser.add_argument("--package-root", type=Path, required=True, action="append")
    parser.add_argument("--output", type=Path, help="New local JSON file; never overwrite an existing file")
    args = parser.parse_args(argv)
    try:
        report = collect(args.assets, args.package_root)
        payload = json.dumps(report, ensure_ascii=True, indent=2) + "\n"
        if args.output:
            ordinary(args.output.parent, directory=True)
            descriptor = os.open(args.output, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as output:
                output.write(payload)
            print("License inventory written; publication/compatibility remains unverified.")
        else:
            print(payload, end="")
        return 0 if report["inventoryComplete"] else 1
    except (InventoryError, OSError) as exc:
        code = str(exc) if isinstance(exc, InventoryError) else "output_or_input_unavailable"
        print("License inventory failed: " + code, file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
