"""Typed tool registry for the technician chat (ARCHITECTURE §4.4).

Each tool is a small spec: name, short description, a pydantic args model, and a handler that returns a
ToolResult. The registry validates arguments, runs the handler, and never raises into the chat loop:
every failure becomes a short ToolResult the model can read.

Tools that are not built yet are registered as *unavailable* stubs. They say so plainly so the model does
not guess (AI safety rule 4).
"""

from __future__ import annotations

import json
import logging
from dataclasses import dataclass, field
from typing import Any, Callable, Iterable, Mapping, Optional

from pydantic import BaseModel, ValidationError

log = logging.getLogger("gse.ai.tools")

MAX_TOOL_RESULT_CHARS = 1500  # small context for a 2B model


@dataclass(frozen=True)
class ToolSource:
    """A citable source produced by a tool. The chat turns these into ADR-008 citations (never the model)."""

    kind: str  # "manual-page" | "work-order"
    display_text: str
    manual_id: Optional[str] = None
    page_start: Optional[int] = None
    work_order_number: Optional[str] = None


@dataclass(frozen=True)
class ToolResult:
    ok: bool
    content: str  # short plain text for the model
    sources: tuple[ToolSource, ...] = field(default_factory=tuple)


@dataclass(frozen=True)
class ToolContext:
    """Runtime dependencies a handler may use. Handlers must not reach for globals."""

    db_path: Optional[Any]  # pathlib.Path | None
    config: Any  # rag.config.Config
    # Query-side embedder from the service (None = BM25-only). Resolved from the cached Ollama probe.
    embedder: Optional[Callable[[list[str]], Optional[list[list[float]]]]] = None


Handler = Callable[[ToolContext, Optional[BaseModel]], ToolResult]  # None only for unavailable stubs


@dataclass(frozen=True)
class ToolSpec:
    name: str
    description: str
    args_model: type[BaseModel]
    handler: Handler
    available: bool = True


def _clip(text: str, limit: int = MAX_TOOL_RESULT_CHARS) -> str:
    return text if len(text) <= limit else text[: limit - 3] + "..."


def _clip_result(result: ToolResult) -> ToolResult:
    return ToolResult(result.ok, _clip(result.content), result.sources)


def _validation_summary(exc: ValidationError) -> str:
    parts = []
    for err in exc.errors()[:3]:
        loc = ".".join(str(p) for p in err.get("loc", ())) or "arguments"
        parts.append(f"{loc}: {err.get('msg', 'invalid')}")
    return "; ".join(parts)


class ToolRegistry:
    def __init__(self, specs: Iterable[ToolSpec]) -> None:
        self._specs: dict[str, ToolSpec] = {}
        for spec in specs:
            if spec.name in self._specs:
                raise ValueError(f"duplicate tool name: {spec.name}")
            if not spec.name.replace("_", "").isalnum():
                raise ValueError(f"tool name must be snake_case: {spec.name}")
            self._specs[spec.name] = spec

    def names(self) -> list[str]:
        return list(self._specs)

    def get(self, name: str) -> Optional[ToolSpec]:
        return self._specs.get(name)

    def schemas(self) -> list[dict[str, Any]]:
        """OpenAI-style function tool definitions (what LiteLLM passes to Ollama)."""
        out = []
        for spec in self._specs.values():
            params = spec.args_model.model_json_schema()
            params.pop("title", None)
            out.append(
                {
                    "type": "function",
                    "function": {"name": spec.name, "description": spec.description, "parameters": params},
                }
            )
        return out

    def invoke(self, name: str, raw_arguments: str | Mapping[str, Any] | None, ctx: ToolContext) -> ToolResult:
        """Run one tool call. Never raises; failures come back as ToolResult(ok=False)."""
        spec = self._specs.get(name)
        if spec is None:
            return ToolResult(False, f"Unknown tool '{name}'. Use only the tools you were given.")
        if not spec.available:
            # Stubs answer the same way whatever the arguments are: the model learns the tool is not available.
            return _clip_result(spec.handler(ctx, None))
        try:
            if isinstance(raw_arguments, Mapping):
                payload: Any = dict(raw_arguments)
            else:
                payload = json.loads(raw_arguments or "{}")
            if not isinstance(payload, dict):
                raise ValueError("arguments must be a JSON object")
            args = spec.args_model.model_validate(payload)
        except json.JSONDecodeError:
            return ToolResult(False, f"Invalid arguments for {name}: invalid JSON.")
        except ValidationError as exc:
            return ToolResult(False, f"Invalid arguments for {name}: {_validation_summary(exc)}.")
        except ValueError as exc:
            return ToolResult(False, f"Invalid arguments for {name}: {exc}.")
        try:
            result = spec.handler(ctx, args)
        except Exception as exc:  # a broken tool must not break the chat turn
            log.warning("tool %s failed: %s", name, type(exc).__name__)
            return ToolResult(False, f"{name} failed; no data was returned.")
        return _clip_result(result)


def unavailable(name: str, description: str, args_model: type[BaseModel], phase: str) -> ToolSpec:
    """A registered stub for a tool whose data arrives in a later phase. It says so; it never guesses."""
    message = (
        f"{name} is not available yet (it needs data from Phase {phase}). "
        "Do not guess. Tell the technician this information is not available yet."
    )

    def _handler(_ctx: ToolContext, _args: BaseModel) -> ToolResult:
        return ToolResult(False, message)

    return ToolSpec(name=name, description=description, args_model=args_model, handler=_handler, available=False)
