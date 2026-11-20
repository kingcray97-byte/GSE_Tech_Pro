"""Technician chat orchestration (ADR-008 /chat, ARCHITECTURE §4.1, §4.5).

Flow: system prompt + short context -> model -> (tool calls -> registry -> tool messages)* -> final text.
Bounded: at most MAX_TOOL_STEPS rounds, then one forced no-tools turn.

Citations are NOT taken from model text. They come only from tool results (manual pages the search returned),
so a model cannot invent a source. If the answer contains a value with a unit and no citation exists, a
deterministic note is appended telling the technician the value is unverified.
"""

from __future__ import annotations

import re
import time
from dataclasses import dataclass
from typing import AbstractSet, Any, Optional

from app.llm import AssistantTurn, ChatBackend
from app.models import ChatRequest, ChatResponse, Citation
from tools.registry import ToolContext, ToolRegistry, ToolSource

MAX_TOOL_STEPS = 3
MAX_TOOL_CALLS_PER_STEP = 4
MAX_HISTORY_MESSAGES = 12
MAX_MESSAGE_CHARS = 2000
MAX_CITATIONS = 8
NO_ANSWER_TEXT = "No answer was produced. Check the cited manual pages or ask again."
UNVERIFIED_NOTE = (
    "Note: no manual source was found for the value above. Treat it as unverified and check the manual."
)

# A number with a unit (pressure, torque, electrical, temperature, flow, length). Conservative on purpose.
_SPEC_LIKE = re.compile(
    r"\b\d+(?:[.,]\d+)?\s?(?:psi|kpa|mpa|bar|in-?lb|ft-?lb|lb-?ft|nm|v|volts?|amps?|a|ohms?|rpm|"
    r"°\s?[cf]|degrees?|mm|in|l/min|gpm|%)\b",
    re.IGNORECASE,
)

_ALLOWED_CLIENT_ROLES = frozenset({"user", "assistant"})
NOT_OFFERED_TOOL = "This tool is not available on this route. Answer from the manual passages you have."


@dataclass
class ChatRun:
    response: ChatResponse
    tool_calls: int = 0
    model_turns: int = 0
    first_token_ms: Optional[float] = None
    total_ms: float = 0.0
    sources_used: int = 0
    unverified_note_added: bool = False


def build_messages(req: ChatRequest, system_prompt: str, *, include_job_context: bool = True) -> list[dict[str, Any]]:
    """System prompt, one short context line, then the most recent client turns (bounded)."""
    system = system_prompt
    if req.system_instruction:
        system += "\n\nApp instruction: " + req.system_instruction.strip()
    if include_job_context:
        context = (
            f"Context: work order {req.work_order_id or 'none'}; asset {req.asset_id or 'none'}. "
            "Look up asset, repair, and part history with the tools; do not guess from memory."
        )
    else:
        context = (
            "Context: job details are not shared on this route. Use manual search for facts; "
            "do not guess values from memory."
        )
    history = [m for m in req.messages if m.role in _ALLOWED_CLIENT_ROLES][-MAX_HISTORY_MESSAGES:]
    # One system message: small local models handle a single system turn more reliably.
    messages: list[dict[str, Any]] = [{"role": "system", "content": f"{system}\n\n{context}"}]
    for m in history:
        messages.append({"role": m.role, "content": m.content[:MAX_MESSAGE_CHARS]})
    return messages


def _to_citation(src: ToolSource) -> Citation:
    if src.kind == "work-order":
        return Citation(kind="work-order", display_text=src.display_text, work_order_number=src.work_order_number)
    if src.kind == "dtc":
        return Citation(kind="dtc", display_text=src.display_text)
    return Citation(
        kind="manual-page",
        display_text=src.display_text,
        manual_id=src.manual_id,
        page_start=src.page_start,
    )


def citations_from_sources(sources: list[ToolSource]) -> list[Citation]:
    seen: set[str] = set()
    out: list[Citation] = []
    for src in sources:
        if src.display_text in seen:
            continue
        seen.add(src.display_text)
        out.append(_to_citation(src))
        if len(out) >= MAX_CITATIONS:
            break
    return out


def apply_unverified_guard(text: str, citations: list[Citation]) -> tuple[str, bool]:
    """Append UNVERIFIED_NOTE when a unit-bearing value appears without any citation."""
    if citations or not _SPEC_LIKE.search(text):
        return text, False
    return f"{text}\n\n{UNVERIFIED_NOTE}".strip(), True


def _tool_name(schema: dict[str, Any]) -> str:
    fn = schema.get("function") if isinstance(schema, dict) else None
    return str(fn.get("name", "")) if isinstance(fn, dict) else ""


def run_chat(
    req: ChatRequest,
    *,
    backend: ChatBackend,
    registry: ToolRegistry,
    tool_ctx: ToolContext,
    system_prompt: str,
    include_job_context: bool = True,
    allowed_tools: Optional[AbstractSet[str]] = None,
) -> ChatRun:
    messages = build_messages(req, system_prompt, include_job_context=include_job_context)
    tools = registry.schemas() if req.use_tools else None
    if tools is not None and allowed_tools is not None:
        tools = [s for s in tools if _tool_name(s) in allowed_tools] or None
    run_sources: list[ToolSource] = []
    tool_calls = 0
    model_turns = 0
    first_token_ms: Optional[float] = None
    started = time.perf_counter()
    text = ""

    for step in range(MAX_TOOL_STEPS + 1):
        offer_tools = tools is not None and step < MAX_TOOL_STEPS
        turn: AssistantTurn = backend.complete(messages, tools if offer_tools else None)
        model_turns += 1
        if first_token_ms is None:
            first_token_ms = turn.first_token_ms
        if not offer_tools or not turn.tool_calls:
            text = turn.text
            break
        messages.append(
            {
                "role": "assistant",
                "content": turn.text,
                "tool_calls": [
                    {"id": c.id, "type": "function", "function": {"name": c.name, "arguments": c.arguments}}
                    for c in turn.tool_calls[:MAX_TOOL_CALLS_PER_STEP]
                ],
            }
        )
        for call in turn.tool_calls[:MAX_TOOL_CALLS_PER_STEP]:
            if allowed_tools is not None and call.name not in allowed_tools:
                # A tool that was not offered on this route returns nothing, so job data never leaves on it.
                messages.append({"role": "tool", "tool_call_id": call.id, "content": NOT_OFFERED_TOOL})
                continue
            result = registry.invoke(call.name, call.arguments, tool_ctx)
            tool_calls += 1
            run_sources.extend(result.sources)
            messages.append({"role": "tool", "tool_call_id": call.id, "content": result.content})

    citations = citations_from_sources(run_sources)
    text = text.strip() or NO_ANSWER_TEXT
    text, unverified = apply_unverified_guard(text, citations)
    response = ChatResponse(text=text, is_suggestion=True, citations=citations)
    return ChatRun(
        response=response,
        tool_calls=tool_calls,
        model_turns=model_turns,
        first_token_ms=first_token_ms,
        total_ms=(time.perf_counter() - started) * 1000.0,
        sources_used=len(citations),
        unverified_note_added=unverified,
    )
