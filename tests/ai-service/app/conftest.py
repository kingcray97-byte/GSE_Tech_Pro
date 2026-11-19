"""HTTP test fixtures for the AI service. Uses FastAPI TestClient with the real lifespan (schema check, probes).

No Ollama is needed: the default probe points at a closed port. Tests that need Ollama-present behaviour inject
a fake probe through `make_client(probe_fn=...)`.
"""

from __future__ import annotations

from typing import Optional

import pytest
from fastapi.testclient import TestClient

from ai_support import TEST_TOKEN, fake_probe, offline_config  # noqa: E402

from app.main import create_app  # noqa: E402


@pytest.fixture
def make_client():
    clients: list[TestClient] = []

    def _make(
        *,
        db_path,
        probe_fn=None,
        backend=None,
        token: str = TEST_TOKEN,
        port: int = 8765,
        overrides: Optional[dict] = None,
        wait_warm: bool = True,
    ) -> TestClient:
        values = {"token": token, "port": port}
        values.update(overrides or {})
        cfg = offline_config(**values)
        # Default: a fake probe that reports Ollama unreachable. No network, so tests stay fast and independent of
        # whatever runs on the machine. The real probe is covered by one explicit test.
        app = create_app(
            cfg,
            db_path=db_path,
            probe_fn=probe_fn if probe_fn is not None else fake_probe(reachable=False),
            backend=backend,
        )
        client = TestClient(app, headers={"X-GSE-Token": token} if token else {})
        client.__enter__()  # run the lifespan (schema check, background warm-up)
        clients.append(client)
        if wait_warm:
            assert app.state.svc.warm.wait(30), "warm-up did not finish"
        return client

    yield _make
    for client in clients:
        client.__exit__(None, None, None)
