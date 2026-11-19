"""Local performance log (ARCHITECTURE: measure response time and memory on the 8 GB target).

Writes one line per chat/search to the `gse.ai.perf` logger (stderr by default). Local only: no network,
no telemetry, and never prompt text, tech data, or the token. Target: first chat token under 3 s.
"""

from __future__ import annotations

import ctypes
import logging
import os
import sys
from typing import Optional

log = logging.getLogger("gse.ai.perf")
FIRST_TOKEN_TARGET_MS = 3000.0


class _ProcessMemoryCounters(ctypes.Structure):  # Windows PROCESS_MEMORY_COUNTERS
    _fields_ = [
        ("cb", ctypes.c_ulong),
        ("PageFaultCount", ctypes.c_ulong),
        ("PeakWorkingSetSize", ctypes.c_size_t),
        ("WorkingSetSize", ctypes.c_size_t),
        ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
        ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
        ("PagefileUsage", ctypes.c_size_t),
        ("PeakPagefileUsage", ctypes.c_size_t),
    ]


def rss_mb() -> Optional[float]:
    """Working set of this process in MB (Windows), or peak RSS on POSIX. None when unknown."""
    try:
        if sys.platform == "win32":
            counters = _ProcessMemoryCounters()
            counters.cb = ctypes.sizeof(counters)
            handle = ctypes.windll.kernel32.GetCurrentProcess()  # type: ignore[attr-defined]
            ok = ctypes.windll.psapi.GetProcessMemoryInfo(  # type: ignore[attr-defined]
                handle, ctypes.byref(counters), counters.cb
            )
            return round(counters.WorkingSetSize / (1024 * 1024), 1) if ok else None
        import resource  # POSIX only

        return round(resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024.0, 1)
    except (OSError, AttributeError, ValueError):
        return None


def log_event(event: str, **fields: object) -> None:
    """One key=value line. Callers pass numbers and short codes only (no content)."""
    parts = [f"event={event}", f"pid={os.getpid()}"]
    rss = rss_mb()
    if rss is not None:
        parts.append(f"rss_mb={rss}")
    for key, value in fields.items():
        if value is None:
            continue
        if isinstance(value, float):
            parts.append(f"{key}={value:.1f}")
        else:
            parts.append(f"{key}={value}")
    log.info(" ".join(parts))
