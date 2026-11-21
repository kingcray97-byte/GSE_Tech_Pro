"""The default tool catalog for the technician chat.

Implemented: `search_manuals` (Phase 3), the Phase 4 GSE tools in tools/gse_tools.py (`get_spec_range`,
`get_safety_warnings`, `lookup_dtc`, `start_guided_test`, `record_reading` (proposal only)), and the Phase 5
repair-history tools in tools/repair_history.py (`find_similar_repairs`, `get_asset`, `get_repair_history`,
`get_part_history`, all read-only), and the Phase 7.3 `ocr_dataplate` in tools/dataplate.py (read-only: the
technician-confirmed data-plate values the app saved on the asset).

`add_note` is still registered as an *unavailable* stub with a typed argument model, so the model sees the real
tool name and gets an honest "not available yet" instead of guessing. A later phase replaces it with a real
handler without changing the registry contract.
"""

from __future__ import annotations

from pydantic import BaseModel, Field

from tools.gse_tools import (
    GET_SAFETY_WARNINGS,
    GET_SPEC_RANGE,
    LOOKUP_DTC,
    RECORD_READING,
    START_GUIDED_TEST,
    GetSafetyWarningsArgs,
    GetSpecRangeArgs,
    LookupDtcArgs,
    RecordReadingArgs,
    StartGuidedTestArgs,
)
from tools.dataplate import OCR_DATAPLATE, OcrDataplateArgs
from tools.manuals import SEARCH_MANUALS
from tools.registry import ToolRegistry, ToolSpec, unavailable
from tools.repair_history import (
    FIND_SIMILAR_REPAIRS,
    GET_ASSET,
    GET_PART_HISTORY,
    GET_REPAIR_HISTORY,
    FindSimilarRepairsArgs,
    GetAssetArgs,
    GetPartHistoryArgs,
    GetRepairHistoryArgs,
)

__all__ = [
    "AddNoteArgs",
    "FindSimilarRepairsArgs",
    "GetAssetArgs",
    "GetPartHistoryArgs",
    "GetRepairHistoryArgs",
    "GetSafetyWarningsArgs",
    "GetSpecRangeArgs",
    "LookupDtcArgs",
    "OcrDataplateArgs",
    "RecordReadingArgs",
    "StartGuidedTestArgs",
    "default_registry",
    "default_specs",
]


class AddNoteArgs(BaseModel):
    scope: str = Field(pattern=r"^(work_order|asset|technician|record|frame)$")
    target_id: str = Field(min_length=1, max_length=64)
    body: str = Field(min_length=1, max_length=2000)


_STUBS = (
    unavailable(
        "add_note",
        "Add a note to a work order, asset, or technician.",
        AddNoteArgs,
        "7",
    ),
)


def default_specs() -> list[ToolSpec]:
    return [
        SEARCH_MANUALS,
        GET_SPEC_RANGE,
        RECORD_READING,
        START_GUIDED_TEST,
        LOOKUP_DTC,
        GET_SAFETY_WARNINGS,
        FIND_SIMILAR_REPAIRS,
        GET_ASSET,
        GET_REPAIR_HISTORY,
        GET_PART_HISTORY,
        OCR_DATAPLATE,
        *_STUBS,
    ]


def default_registry() -> ToolRegistry:
    return ToolRegistry(default_specs())
