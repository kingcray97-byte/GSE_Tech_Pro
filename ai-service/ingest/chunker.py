"""Section-aware chunking and fine-grained chunk-kind heuristics.

Rules:
- Numbered steps ("2.20 Check for any loose bolts ...") are atomic: a chunk boundary is only placed
  before a step, never inside one. Free text splits at blank lines.
- Chunks target ~900 chars and never exceed ~1800 (unless one atomic unit is larger, then it is split
  at line boundaries only).
- Chunks never span a page break; page_start == page_end for every chunk from a text page (1-based PDF page).
- chunk_kind is the primary fine-grained kind; `kinds` lists every heuristic hit (stored as kind:* tags).
- section is the coarse category (specs/troubleshooting/procedures/wiring/torque) or None.
"""

from __future__ import annotations

import re
from dataclasses import dataclass, field

from ingest.pdf_text import PageText

MAX_CHARS = 1800
TARGET_CHARS = 900

# "2.20 Title", bare "2.20" (title on next line). Two decimal digits, as in this manual family
# ("1.40", "2.20"), so values such as "0.8 Inch" or "1.0 Inch" in running text are not steps.
# Other numbering schemes (e.g. "3.2") must be verified per manual before relying on step detection.
STEP_RE = re.compile(r"^(\d{1,2}\.\d{2}(?:\.\d{1,2})?)(?:\s*$|\s+(?=[A-Za-z]))")

KIND_ORDER = ("diagram_caption", "wiring", "torque", "troubleshooting", "specs", "table", "procedure", "text")
COARSE_SECTION = {
    "specs": "specs",
    "troubleshooting": "troubleshooting",
    "wiring": "wiring",
    "torque": "torque",
    "procedure": "procedures",
}

_CAPTION_RE = re.compile(r"^(illustration|figure|fig\.|drawing no|diagram)\b", re.IGNORECASE)
_TORQUE_RE = re.compile(
    r"\btorque\b|\b\d+(?:[.,]\d+)?\s*(?:n\s*·?\s*m|n\.m|ft[- ]?lb|in[- ]?lb|lb[- ]?ft)\b",
    re.IGNORECASE,
)
_SPEC_UNIT_RE = re.compile(
    r"\b\d+(?:[.,]\d+)?\s*(?:psi|kpa|mpa|bar|vdc|vac|volts?|v|amps?|ma|mm|rpm|hz|ohms?|°\s*[cf]|%)(?=\W|$)",
    re.IGNORECASE,
)
_SPEC_WORD_RE = re.compile(r"\b(specification|tolerance|clearance|nominal|min\.|max\.)\b", re.IGNORECASE)
_TROUBLE_RE = re.compile(
    r"\b(troubleshoot\w*|symptoms?|possible causes?|remedy|fault|no (?:power|hydraulic|movement)|"
    r"does not|doesn't|not working)\b",
    re.IGNORECASE,
)
_WIRING_RE = re.compile(
    r"\b(wire|wiring|harness|connector|pinout|circuit|schematic|terminal|fuse|relay|ground)\b|\bpin\s*\d+\b",
    re.IGNORECASE,
)
_PROC_VERB_RE = re.compile(
    r"\b(remove|install|disconnect|loosen|tighten|lubricate|replace|check|adjust|inspect|clean|extract|test)\b",
    re.IGNORECASE,
)
_TABLE_ROW_RE = re.compile(r"\S {3,}\S.*\S {3,}\S")


@dataclass
class Block:
    kind: str  # "step" (atomic numbered step) | "para"
    page: int
    lines: list[str]

    @property
    def text(self) -> str:
        return "\n".join(self.lines)


@dataclass
class ChunkDraft:
    page_start: int
    page_end: int
    section: str | None
    heading: str | None
    chunk_kind: str
    kinds: frozenset[str]
    text: str
    pages: list[int] = field(default_factory=list)


def classify(text: str) -> tuple[str, frozenset[str]]:
    """Return (primary chunk_kind, all heuristic kinds). Falls back to 'text'."""
    lines = [ln.strip() for ln in text.splitlines() if ln.strip()]
    hits: set[str] = set()

    if lines and all(_CAPTION_RE.match(ln) for ln in lines) and len(text) < 400:
        hits.add("diagram_caption")
    if len(_WIRING_RE.findall(text)) >= 2:
        hits.add("wiring")
    if _TORQUE_RE.search(text):
        hits.add("torque")
    if _TROUBLE_RE.search(text):
        hits.add("troubleshooting")
    if len(_SPEC_UNIT_RE.findall(text)) >= 2 or _SPEC_WORD_RE.search(text):
        hits.add("specs")
    if sum(1 for ln in lines if _TABLE_ROW_RE.search(ln)) >= 4:
        hits.add("table")
    if any(STEP_RE.match(ln) for ln in lines) or len(_PROC_VERB_RE.findall(text)) >= 2:
        hits.add("procedure")

    primary = next((k for k in KIND_ORDER if k in hits), "text")
    return primary, frozenset(hits)


def build_blocks(pages: list[PageText]) -> list[Block]:
    blocks: list[Block] = []
    for page in pages:
        if page.scanned or page.blank:
            continue
        cur: Block | None = None
        for raw in page.lines:
            s = raw.strip()
            if not s:
                if cur is not None and cur.kind == "para":
                    blocks.append(cur)
                    cur = None
                continue  # blank lines inside a step do not end the step
            if STEP_RE.match(s):
                if cur is not None:
                    blocks.append(cur)
                cur = Block("step", page.number, [s])
            elif cur is None:
                cur = Block("para", page.number, [s])
            else:
                cur.lines.append(s)
        if cur is not None:
            blocks.append(cur)  # blocks never span pages
    return blocks


def _split_oversized(block: Block) -> list[Block]:
    if len(block.text) <= MAX_CHARS:
        return [block]
    pieces: list[Block] = []
    lines: list[str] = []
    size = 0
    for line in block.lines:
        if lines and size + len(line) + 1 > MAX_CHARS:
            pieces.append(Block(block.kind if not pieces else "para", block.page, lines))
            lines, size = [], 0
        lines.append(line)
        size += len(line) + 1
    if lines:
        pieces.append(Block(block.kind if not pieces else "para", block.page, lines))
    return pieces


def _heading(block: Block) -> str:
    first = block.lines[0]
    if block.kind == "step" and STEP_RE.match(first) and first == STEP_RE.match(first).group(1):
        # bare step number: the title is on the following line
        if len(block.lines) > 1:
            first = f"{first} {block.lines[1]}"
    return first[:160]


def _make_chunk(pieces: list[Block]) -> ChunkDraft:
    text = "\n".join(p.text for p in pieces)
    primary, kinds = classify(text)
    pages = sorted({p.page for p in pieces})
    return ChunkDraft(
        page_start=pages[0],
        page_end=pages[-1],
        section=COARSE_SECTION.get(primary),
        heading=_heading(pieces[0]),
        chunk_kind=primary,
        kinds=kinds,
        text=text,
        pages=pages,
    )


def chunk_pages(pages: list[PageText]) -> list[ChunkDraft]:
    chunks: list[ChunkDraft] = []
    cur: list[Block] = []
    size = 0
    for block in build_blocks(pages):
        for piece in _split_oversized(block):
            n = len(piece.text) + 1
            # Chunks never span a page break, so every citation names exactly the page it came from.
            new_page = bool(cur) and piece.page != cur[-1].page
            if cur and (new_page or size + n > MAX_CHARS or (piece.kind == "step" and size >= TARGET_CHARS)):
                chunks.append(_make_chunk(cur))
                cur, size = [], 0
            cur.append(piece)
            size += n
    if cur:
        chunks.append(_make_chunk(cur))
    return chunks
