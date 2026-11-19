"""Guided troubleshooting sessions (ADR-008 /guided/*, ADR-009 §3).

The service holds only ephemeral session state here: `session_id -> { tree_id, current_step_id }`. It persists
nothing. The trees, steps, test points and safety warnings are read from the shared SQLite database (see
app/troubleshooting.py). A session without a tree answers with the explicit "no tree yet" step.
"""

from __future__ import annotations

import threading
import uuid
from dataclasses import dataclass
from typing import Optional

NO_TREE_MESSAGE = "No troubleshooting tree is available for this model and symptom yet."
MAX_SESSIONS = 200  # bounded memory on the tablet; the oldest session is dropped first


@dataclass
class GuidedSession:
    session_id: str
    equipment_model_id: str
    symptom_id: Optional[str]
    complaint_text: Optional[str]
    tree_id: Optional[str] = None
    current_step_id: Optional[str] = None


class GuidedStore:
    def __init__(self, max_sessions: int = MAX_SESSIONS) -> None:
        self._max = max_sessions
        self._lock = threading.Lock()
        self._sessions: dict[str, GuidedSession] = {}

    def start(self, equipment_model_id: str, symptom_id: str | None, complaint_text: str | None) -> GuidedSession:
        session = GuidedSession(str(uuid.uuid4()), equipment_model_id, symptom_id, complaint_text)
        with self._lock:
            if len(self._sessions) >= self._max:
                self._sessions.pop(next(iter(self._sessions)))
            self._sessions[session.session_id] = session
        return session

    def get(self, session_id: str) -> GuidedSession | None:
        with self._lock:
            return self._sessions.get(session_id)

    def attach(self, session_id: str, tree_id: str, step_id: str) -> None:
        """Record where the session is in the tree. Unknown sessions are ignored (the caller already 404s)."""
        with self._lock:
            session = self._sessions.get(session_id)
            if session is not None:
                session.tree_id = tree_id
                session.current_step_id = step_id
