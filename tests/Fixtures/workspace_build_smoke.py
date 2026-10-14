"""Explicit, local HTTP integration check; actually builds the trusted sample.

Start Tenka with TENKA_ALLOWED_ROOTS containing this repository,
TENKA_TRUST_MSBUILD=1 and TENKA_ENABLE_LOCAL_EXEC=1 before running.
This script is NOT run automatically by CI or by unit tests.
"""
import argparse
import json
from pathlib import Path
from urllib.error import HTTPError
from urllib.parse import urlparse
from urllib.request import Request, urlopen


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", default="http://127.0.0.1:5077")
    parser.add_argument("--approve-host-build", action="store_true", required=True)
    parser.add_argument("--allow-restore", action="store_true")
    args = parser.parse_args()
    parsed = urlparse(args.base)
    if parsed.scheme != "http" or parsed.hostname not in ("127.0.0.1", "localhost", "::1") or parsed.username or parsed.password or parsed.path not in ("", "/") or parsed.query or parsed.fragment:
        parser.error("Use a loopback HTTP Tenka dashboard URL without credentials, path, query or fragment.")
    base = args.base.rstrip("/")
    token = ""

    def call(path: str, body: dict | None = None, expected: int = 200) -> dict:
        headers = {"Accept": "application/json"}
        data = None
        if body is not None:
            headers.update({"Content-Type": "application/json", "X-Tenka-CSRF": token, "Origin": base})
            data = json.dumps(body).encode()
        req = Request(base + path, data=data, headers=headers)
        try:
            with urlopen(req, timeout=360) as response:
                status, raw = response.status, response.read()
        except HTTPError as error:
            status, raw = error.code, error.read()
        assert status == expected, (path, status, raw.decode(errors="replace"))
        return json.loads(raw)

    token = call("/api/session")["csrfToken"]
    project = str(Path(__file__).resolve().parent / "WorkspaceBuildSample" / "WorkspaceBuildSample.csproj")
    call("/api/workspace/open", {"path": project})
    options = call("/api/workspace/build-options")
    assert options["loaded"] and options["buildEnabled"], options
    request = dict(expectedSnapshotId=options["snapshotId"], projectPath=project,
                   targetFramework="net8.0", allowRestore=args.allow_restore,
                   approveUnisolatedBuild=False, analyzeAfterBuild=True, timeoutSeconds=180)
    call("/api/workspace/build", request, expected=403)
    request["approveUnisolatedBuild"] = True
    call("/api/workspace/build", dict(request, expectedSnapshotId="stale"), expected=409)
    result = call("/api/workspace/build", request)
    assert result["build"]["status"] == "build_succeeded", result
    assert result["workspaceReloaded"] and result["analysis"] is not None, result
    assert result["snapshotId"] != options["snapshotId"], result
    assert any(Path(a["assemblyPath"]).name == "WorkspaceBuildSample.dll" for a in result["assemblies"]), result
    call("/api/workspace/build", request, expected=409)
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
