"""X-GSE-Token enforcement (ADR-008): every endpoint, including /health and /config.

Implemented as pure ASGI middleware so it runs before routing and body parsing: unknown paths and
malformed bodies also require the token, and nothing is revealed about routes to an unauthenticated
caller. The comparison is constant-time and the token is never logged.
"""

from __future__ import annotations

import hmac
import json

TOKEN_HEADER = b"x-gse-token"  # ASGI header names are lowercased bytes


def token_matches(provided: str | None, expected: str) -> bool:
    """Constant-time comparison. Blank/missing tokens never match (even if expected were blank)."""
    if not provided or not provided.strip() or not expected:
        return False
    return hmac.compare_digest(provided.encode("utf-8"), expected.encode("utf-8"))


class TokenAuthMiddleware:
    def __init__(self, app, token: str) -> None:  # noqa: ANN001
        if not token or not token.strip():
            # Defense in depth: create_app refuses first, but never build an open middleware.
            raise ValueError("refusing to build an unauthenticated service (empty token)")
        self.app = app
        self._token = token

    async def __call__(self, scope, receive, send):  # noqa: ANN001
        if scope["type"] not in ("http", "websocket"):
            await self.app(scope, receive, send)
            return
        provided = None
        for name, value in scope.get("headers", []):
            if name == TOKEN_HEADER:
                try:
                    provided = value.decode("latin-1")
                except UnicodeDecodeError:
                    provided = None
                break
        if token_matches(provided, self._token):
            await self.app(scope, receive, send)
            return
        await _send_unauthorized(scope, receive, send)


async def _send_unauthorized(scope, receive, send) -> None:  # noqa: ANN001
    body = json.dumps(
        {
            "error": {
                "code": "unauthorized",
                "message": "Missing or invalid X-GSE-Token.",
                "detail": None,
            }
        }
    ).encode("utf-8")
    if scope["type"] == "websocket":
        await send({"type": "websocket.close", "code": 1008})
        return
    # Drain the request body so the connection is reusable.
    more = True
    while more:
        message = await receive()
        more = message.get("more_body", False)
    await send(
        {
            "type": "http.response.start",
            "status": 401,
            "headers": [
                (b"content-type", b"application/json; charset=utf-8"),
                (b"content-length", str(len(body)).encode("ascii")),
                (b"www-authenticate", b'GSE-Token realm="gse-ai"'),
            ],
        }
    )
    await send({"type": "http.response.body", "body": body})
