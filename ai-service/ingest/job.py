"""Ingestion job status, matching ADR-008 `IngestionStatus`.

Pause/resume/cancel are internal controls. Pause is reported on the wire as state "running" because
ADR-008 defines no paused state (open question for architect).
"""

from __future__ import annotations

import threading
import time
import uuid
from dataclasses import dataclass
from typing import Any

STATES = ("pending", "running", "succeeded", "failed", "cancelled")


@dataclass
class IngestError:
    file: str
    message: str
    page: int | None = None

    def to_dict(self) -> dict[str, Any]:
        return {"file": self.file, "message": self.message, "page": self.page}


class IngestionJob:
    def __init__(self, job_id: str | None = None) -> None:
        self.job_id = job_id or str(uuid.uuid4())
        self.state = "pending"
        self.files_total = 0
        self.files_done = 0
        self.chunks_created = 0
        self.chunks_embedded = 0
        self.current_file: str | None = None
        self.errors: list[IngestError] = []
        # Internal informational notes (duplicates skipped, embedding unavailable). Not on the wire.
        self.notes: list[str] = []
        self._lock = threading.Lock()
        self._running = threading.Event()
        self._running.set()
        self._cancel = threading.Event()

    def add_error(self, file: str, message: str, page: int | None = None) -> None:
        with self._lock:
            self.errors.append(IngestError(file=file, message=message, page=page))

    def add_note(self, message: str) -> None:
        with self._lock:
            self.notes.append(message)

    def to_status(self) -> dict[str, Any]:
        with self._lock:
            return {
                "job_id": self.job_id,
                "state": self.state,
                "files_total": self.files_total,
                "files_done": self.files_done,
                "chunks_created": self.chunks_created,
                "chunks_embedded": self.chunks_embedded,
                "current_file": self.current_file,
                "errors": [e.to_dict() for e in self.errors],
            }

    @property
    def paused(self) -> bool:
        return not self._running.is_set()

    @property
    def cancel_requested(self) -> bool:
        return self._cancel.is_set()

    def pause(self) -> None:
        self._running.clear()

    def resume(self) -> None:
        self._running.set()

    def cancel(self) -> None:
        self._cancel.set()
        self._running.set()  # wake a paused worker so it can observe the cancel

    def wait_if_paused(self, poll_s: float = 0.2) -> bool:
        """Block while paused. Returns False when cancelled (caller stops), True to continue."""
        while not self._running.is_set():
            if self._cancel.is_set():
                return False
            time.sleep(poll_s)
        return not self._cancel.is_set()


MAX_REGISTRY = 100  # keep only the most recent jobs in memory

_REGISTRY: dict[str, IngestionJob] = {}  # insertion-ordered: oldest first
_REGISTRY_LOCK = threading.Lock()


def register_job(job: IngestionJob) -> None:
    with _REGISTRY_LOCK:
        _REGISTRY[job.job_id] = job
        while len(_REGISTRY) > MAX_REGISTRY:
            _REGISTRY.pop(next(iter(_REGISTRY)))


def get_job(job_id: str) -> IngestionJob:
    """Raises KeyError for unknown jobs (the endpoint maps this to 404 not_found)."""
    with _REGISTRY_LOCK:
        return _REGISTRY[job_id]
