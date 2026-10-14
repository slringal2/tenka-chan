"""End-to-end sanity for one-click discovery and explicit host experiment authorization."""
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[2]

def main():
    with tempfile.TemporaryDirectory(prefix="tenka-quick-ui-") as tmp:
        env = os.environ.copy()
        env.update(TENKA_ALLOWED_ROOTS=str(ROOT),
                   TENKA_TRUST_MSBUILD="1",
                   TENKA_ENABLE_LOCAL_EXEC="1",
                   TENKA_ENABLE_UNSANDBOXED_EXPERIMENTS="1",
                   TENKA_ENABLE_DOCKER="0",
                   TENKA_DATA_DIR=tmp)
        with socket.socket() as sock:
            sock.bind(("127.0.0.1",0))
            port = sock.getsockname()[1]
        env["TENKA_WEB_PORT"] = str(port)
        base = f"http://127.0.0.1:{port}"
        token = ""
        def req(path, body=None, expect=200, csrf=True):
            headers = {}
            if body is not None:
                headers["Content-Type"] = "application/json"
                if csrf: headers["X-Tenka-CSRF"] = token
            request = urllib.request.Request(base+path,
                data=json.dumps(body).encode() if body is not None else None,
                headers=headers)
            try:
                with urllib.request.urlopen(request,timeout=80) as r:
                    status,payload=r.status,r.read()
            except urllib.error.HTTPError as e:
                status,payload=e.code,e.read()
            assert status==expect,(path,status,expect,payload[:1200])
            return json.loads(payload) if payload and path.startswith("/api/") else payload
        with open(Path(tmp)/"web.log","w",encoding="utf-8") as log:
            app=subprocess.Popen(["dotnet",str(ROOT/"src/TenkaChan.Web/bin/Release/net10.0/TenkaChan.Web.dll")],
                cwd=ROOT/"src/TenkaChan.Web",env=env,stdout=log,stderr=subprocess.STDOUT)
            try:
                for _ in range(100):
                    if app.poll() is not None: raise RuntimeError("Web server stopped early")
                    try:
                        session=req("/api/session")
                        token=session["csrfToken"];break
                    except (ConnectionError,urllib.error.URLError):
                        time.sleep(.2)
                else: raise RuntimeError("Web UI did not start")
                assert session["hostExperimentsEnabled"] is True
                assert session["dockerEnabled"] is False
                fixture=ROOT/"tests/Fixtures/PrioritySample/PrioritySample.csproj"
                req("/api/workspace/open",{"path":str(fixture)})
                config=req("/api/workspace/quick-config")
                assert config["assemblies"] and not config["assemblies"][0]["runnable"]
                assert any(":Run" in x["jitFilter"] for x in config["jitMethods"])
                for name in ("/","/trace.html","/assembly.html"):
                    assert req(name,expect=200)
                req("/api/workspace/scan",{})
                req("/api/hotspots/scan",{"maxMethods":30,"maxSites":100})
                receipt=req("/api/experiments/prepare",{
                    "repositoryRoot":str(fixture.parent),
                    "projectRelativePath":fixture.name,
                    "kind":"test","generatedCode":
                        "using Xunit; public class Hello { [Fact] public void Check() { Assert.True(true); } }",
                    "targetFramework":"net8.0"
                })
                req("/api/experiments/"+receipt["id"]+"/run-host",
                    {"approveUnisolatedExecution":False},expect=400)
                req("/api/experiments/"+receipt["id"]+"/run-host",
                    {"approveUnisolatedExecution":True},expect=403,csrf=False)
                print("One-click DLL discovery, HTTP static scan and host permission gates passed.")
            finally:
                app.terminate()
                try:app.wait(timeout=8)
                except subprocess.TimeoutExpired:app.kill();app.wait(timeout=8)
if __name__=="__main__":main()
