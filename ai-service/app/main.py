"""GSE Tech Pro AI service entrypoint (ADR-008).

Run (standalone / dev):   python -m app.main --port 8765 --token <token>
                          python -m app.main --config app/config.yaml
The App launches the same entrypoint with --port and --token; CLI values override config.yaml.

The service binds 127.0.0.1 only (enforced by config validation), requires X-GSE-Token on every endpoint,
refuses to start with an empty token, and boots without Ollama (degraded, never crashing).
"""

from __future__ import annotations

import argparse
import logging
import os
import sys
import threading
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Any, AsyncIterator, Optional, Sequence

from fastapi import FastAPI

from app import SERVICE_VERSION
from app.api import router
from app.bootstrap import ensure_schema
from app.errors import install_error_handlers
from app.llm import IdleUnloader, OllamaChatBackend
from app.ollama import OllamaMonitor, probe_now
from app.prompts import load_prompt
from app.security import TokenAuthMiddleware
from app.state import ServiceState
from rag.config import Config, load_config
from rag.db import default_db_path
from tools.catalog import default_registry
from tools.registry import ToolRegistry

log = logging.getLogger("gse.ai")
DEFAULT_CONFIG_PATH = Path(__file__).resolve().with_name("config.yaml")


class RefuseToServe(RuntimeError):
    """Raised instead of serving when the service would run unauthenticated or misconfigured."""


def create_app(
    config: Config,
    *,
    db_path: Optional[Path | str] = None,
    migrations_dir: Optional[Path | str] = None,
    backend: Any = None,
    probe_fn: Any = None,
    registry: Optional[ToolRegistry] = None,
    system_prompt: Optional[str] = None,
) -> FastAPI:
    """Build the FastAPI app. Injection points exist for tests; the defaults are the production wiring."""
    if not config.service.token or not config.service.token.strip():
        raise RefuseToServe("no service token: pass --token or set service.token (refusing to serve unauthenticated)")
    config.validate()
    if config.ollama.no_cloud:
        os.environ["OLLAMA_NO_CLOUD"] = "1"

    resolved_db = Path(db_path) if db_path is not None else default_db_path()
    state = ServiceState(
        config=config,
        db_path=resolved_db,
        monitor=OllamaMonitor(config.ollama.base_url, probe_fn=probe_fn or probe_now),
        backend=backend if backend is not None else OllamaChatBackend(config),
        registry=registry if registry is not None else default_registry(),
        system_prompt=system_prompt if system_prompt is not None else load_prompt("system.md"),
    )
    state.unloader = IdleUnloader(config.idle_unload_minutes, state.backend.unload)
    migrations = Path(migrations_dir) if migrations_dir is not None else None

    @asynccontextmanager
    async def lifespan(_app: FastAPI) -> AsyncIterator[None]:
        try:
            report = ensure_schema(resolved_db, migrations)
            if report.applied:
                log.info("schema: applied %s", report.applied)
        except Exception as exc:  # degraded, not dead: search reports db_unavailable
            state.schema_error = type(exc).__name__
            log.warning("schema bootstrap failed (%s); search will report db_unavailable", state.schema_error)
        threading.Thread(target=state.warm_up, name="ollama-warmup", daemon=True).start()
        threading.Thread(target=state.unload_loop, name="idle-unload", daemon=True).start()
        log.info("AI service %s ready on %s:%s (chat=%s, embed=%s)", SERVICE_VERSION, config.service.host,
                 config.service.port, config.models.chat, config.models.embedding)
        try:
            yield
        finally:
            state.stop.set()

    app = FastAPI(
        title="GSE Tech Pro AI service",
        version=SERVICE_VERSION,
        lifespan=lifespan,
        redirect_slashes=False,
    )
    app.state.svc = state
    install_error_handlers(app)
    app.include_router(router)
    app.add_middleware(TokenAuthMiddleware, token=config.service.token)
    return app


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    p = argparse.ArgumentParser(prog="gse-ai", description="GSE Tech Pro local AI service (127.0.0.1 only).")
    p.add_argument("--config", default=str(DEFAULT_CONFIG_PATH), help="path to config.yaml")
    p.add_argument("--port", type=int, default=None, help="override service.port")
    p.add_argument("--token", default=None, help="override service.token (per-install X-GSE-Token)")
    p.add_argument("--db", default=None, help="SQLite database path (default: GSE_TECH_PRO_DB or %%LOCALAPPDATA%%)")
    p.add_argument("--migrations", default=None, help="folder with 00N_*.sql migrations (safety net)")
    p.add_argument("--log-level", default="INFO", choices=["DEBUG", "INFO", "WARNING", "ERROR"])
    return p.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    args = parse_args(argv)
    logging.basicConfig(
        level=getattr(logging, args.log_level),
        format="%(asctime)s %(levelname)s %(name)s %(message)s",
        stream=sys.stderr,
    )
    logging.getLogger("httpx").setLevel(logging.WARNING)  # per-request URL lines are noise; never log bodies
    try:
        config = load_config(args.config, overrides={"port": args.port, "token": args.token})
    except (OSError, ValueError) as exc:
        print(f"gse-ai: configuration error: {exc}", file=sys.stderr)
        return 2
    if not config.service.token.strip():
        print("gse-ai: refusing to start: the service token is empty (pass --token).", file=sys.stderr)
        return 3
    try:
        app = create_app(
            config,
            db_path=args.db,
            migrations_dir=args.migrations,
        )
    except RefuseToServe as exc:
        print(f"gse-ai: refusing to start: {exc}", file=sys.stderr)
        return 3

    import uvicorn  # imported here so tests of create_app do not start a server stack

    uvicorn.run(
        app,
        host=config.service.host,  # validated loopback (never 0.0.0.0)
        port=config.service.port,
        log_level="warning",
        access_log=False,
        server_header=False,
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
