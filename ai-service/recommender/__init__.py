"""Repair-history recommendation engine (ADR-010). Read-only on the shared DB; no model involved.

Public entry point for the HTTP route and the chat tool:
    from recommender import run_repair_history_search, UnknownWorkOrderError
"""

from recommender.search import (
    DEFAULT_TOP_K,
    MAX_TOP_K,
    UnknownWorkOrderError,
    run_repair_history_search,
)

__all__ = ["run_repair_history_search", "UnknownWorkOrderError", "DEFAULT_TOP_K", "MAX_TOP_K"]
