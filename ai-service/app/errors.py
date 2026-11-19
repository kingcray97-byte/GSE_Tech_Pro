"""ADR-008 error envelope: every non-2xx response is `{ "error": { code, message, detail } }`.

The app acts on HTTP status + `code`, never on the message text.
"""

from __future__ import annotations

from typing import Optional

from fastapi import FastAPI, Request
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse
from starlette.exceptions import HTTPException as StarletteHTTPException

# ADR-008 HTTP status -> code mapping. 500 is split by code at the raise site.
STATUS_CODES = {
    400: "invalid_request",
    401: "unauthorized",
    404: "not_found",
    409: "conflict",
    422: "validation_error",
    500: "internal",
    503: "ai_unavailable",
}


class ApiError(Exception):
    def __init__(
        self,
        status: int,
        code: str,
        message: str,
        detail: Optional[str] = None,
    ) -> None:
        super().__init__(message)
        self.status = status
        self.code = code
        self.message = message
        self.detail = detail


def envelope(code: str, message: str, detail: Optional[str] = None) -> dict:
    return {"error": {"code": code, "message": message, "detail": detail}}


def json_error(status: int, code: str, message: str, detail: Optional[str] = None) -> JSONResponse:
    return JSONResponse(
        status_code=status,
        content=envelope(code, message, detail),
        media_type="application/json; charset=utf-8",
    )


def install_error_handlers(app: FastAPI) -> None:
    @app.exception_handler(ApiError)
    async def _api_error(_: Request, exc: ApiError) -> JSONResponse:  # noqa: ARG001
        return json_error(exc.status, exc.code, exc.message, exc.detail)

    @app.exception_handler(RequestValidationError)
    async def _validation(_: Request, exc: RequestValidationError) -> JSONResponse:
        # Only the field location and problem; never echo the submitted values (may contain tech data).
        problems = "; ".join(
            f"{'.'.join(str(p) for p in err.get('loc', ())[1:])}: {err.get('msg', 'invalid')}"
            for err in exc.errors()[:5]
        )
        return json_error(422, "validation_error", "Request validation failed.", problems or None)

    @app.exception_handler(StarletteHTTPException)
    async def _http(_: Request, exc: StarletteHTTPException) -> JSONResponse:
        code = STATUS_CODES.get(exc.status_code, "internal")
        message = exc.detail if isinstance(exc.detail, str) else "Request failed."
        return json_error(exc.status_code, code, message)

    @app.exception_handler(Exception)
    async def _unhandled(_: Request, exc: Exception) -> JSONResponse:
        # Never leak a traceback or the exception text to the client; the logger has the detail.
        import logging

        logging.getLogger("gse.ai").exception("unhandled error: %s", type(exc).__name__)
        return json_error(500, "internal", "Internal AI service error.")
