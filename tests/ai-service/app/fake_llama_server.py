"""A fake llama-server for tests (ADR-012): real HTTP on 127.0.0.1, no model.

- `GET /health`: 200 when state is "ready", 503 when "loading".
- `POST /v1/chat/completions`: OpenAI-style SSE stream of `reply` (only when ready).
- `POST /v1/embeddings`: OpenAI-style vectors of `dim` floats (only when ready).
Stopping the server makes it "down" (connection refused), like a llama-server that was never started.
"""

from __future__ import annotations

import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Optional


class FakeLlamaServer:
    def __init__(self, *, state: str = "ready", reply: str = "From the tablet.", dim: int = 768) -> None:
        self.state = state
        self.reply = reply
        self.dim = dim
        self.requests: list[tuple[str, Optional[dict]]] = []
        fake = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *_args):  # keep test output clean
                pass

            def _send(self, code: int, body: bytes, ctype: str = "application/json") -> None:
                self.send_response(code)
                self.send_header("Content-Type", ctype)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def do_GET(self):  # noqa: N802
                fake.requests.append((self.path, None))
                if self.path == "/health":
                    if fake.state == "ready":
                        self._send(200, b'{"status":"ok"}')
                    else:
                        self._send(503, b'{"error":{"code":503,"message":"Loading model"}}')
                else:
                    self._send(404, b"{}")

            def do_POST(self):  # noqa: N802
                length = int(self.headers.get("Content-Length") or 0)
                payload = json.loads(self.rfile.read(length) or b"{}")
                fake.requests.append((self.path, payload))
                if fake.state != "ready":
                    self._send(503, b'{"error":{"code":503,"message":"Loading model"}}')
                    return
                if self.path == "/v1/embeddings":
                    inputs = payload.get("input") or []
                    data = [{"object": "embedding", "index": i, "embedding": [0.01 * (i + 1)] * fake.dim}
                            for i in range(len(inputs))]
                    self._send(200, json.dumps({"object": "list", "data": data, "model": payload.get("model")}).encode())
                elif self.path == "/v1/chat/completions":
                    chunk = {"id": "c1", "object": "chat.completion.chunk", "created": 0, "model": payload.get("model"),
                             "choices": [{"index": 0, "delta": {"role": "assistant", "content": fake.reply},
                                          "finish_reason": None}]}
                    end = {"id": "c1", "object": "chat.completion.chunk", "created": 0, "model": payload.get("model"),
                           "choices": [{"index": 0, "delta": {}, "finish_reason": "stop"}]}
                    body = f"data: {json.dumps(chunk)}\n\ndata: {json.dumps(end)}\n\ndata: [DONE]\n\n".encode()
                    self._send(200, body, "text/event-stream")
                else:
                    self._send(404, b"{}")

        self._httpd = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.port = self._httpd.server_address[1]
        self._thread = threading.Thread(target=self._httpd.serve_forever, daemon=True)

    @property
    def base_url(self) -> str:
        return f"http://127.0.0.1:{self.port}"

    def __enter__(self) -> "FakeLlamaServer":
        self._thread.start()
        return self

    def __exit__(self, *_exc) -> None:
        self._httpd.shutdown()
        self._httpd.server_close()
