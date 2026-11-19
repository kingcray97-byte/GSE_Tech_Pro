"""Loads prompt files from ai-service/prompts/ (prompts are files, never inline strings)."""

from __future__ import annotations

from pathlib import Path

PACKAGE_ROOT = Path(__file__).resolve().parents[1]  # ai-service/
PROMPTS_DIR = PACKAGE_ROOT / "prompts"
MAX_PROMPT_CHARS = 4000  # a 2B model's context is small; a bloated prompt is a bug


def load_prompt(name: str, directory: Path = PROMPTS_DIR) -> str:
    """Read `<directory>/<name>` and return it stripped. Raises FileNotFoundError if missing."""
    if "/" in name or "\\" in name or not name.endswith(".md"):
        raise ValueError("prompt name must be a bare *.md file name")
    text = (directory / name).read_text(encoding="utf-8").strip()
    if not text:
        raise ValueError(f"prompt file is empty: {name}")
    if len(text) > MAX_PROMPT_CHARS:
        raise ValueError(f"prompt file too long for the 2B model: {name} ({len(text)} chars)")
    return text
