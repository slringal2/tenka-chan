#!/usr/bin/env python3
"""Trusted CI fixture: localhost-only OpenAI-compatible chat completion stub."""

import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

REPLY = {
    "hypotheses": [
        {
            "category": "correctness",
            "claim": "Investigate behavior for unexpected argument values",
            "failureCondition": "The method violates its caller's documented invariants",
            "reason": "Provisional review hypothesis from the isolated CI fake model.",
            "testIdeas": ["Compare normal and boundary inputs with a trusted oracle"],
        }
    ],
    "notes": "Fixture output. Never treat as a confirmed defect.",
}


class Handler(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        return

    def do_GET(self):
        if self.path != "/health":
            self.send_error(404)
            return
        body = b'{"status":"ok"}'
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        if self.path != "/v1/chat/completions":
            self.send_error(404)
            return
        size = int(self.headers.get("Content-Length", "0"))
        if size < 1 or size > 128_000:
            self.send_error(413)
            return
        payload = json.loads(self.rfile.read(size))
        assert payload.get("model") == "ci-fixture", payload.get("model")
        assert payload.get("messages") and len(payload["messages"]) >= 2

        body = json.dumps(
            {"choices": [{"message": {"role": "assistant", "content": json.dumps(REPLY)}}]}
        ).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


if __name__ == "__main__":
    ThreadingHTTPServer(("127.0.0.1", 18081), Handler).serve_forever()
