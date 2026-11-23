"""Checks a built embedded Python in the installed layout (run by build-python-embedded.ps1).

Expects <stage>\\Python\\python.exe (this interpreter) next to <stage>\\AiService, exactly as setup installs
{app}\\Python and {app}\\AiService. Exits non-zero on the first thing the tablet would trip over.
"""

from __future__ import annotations

import os
import sqlite3
import subprocess
import sys
import tempfile
from pathlib import Path

PYTHON_DIR = Path(sys.executable).resolve().parent
SERVICE_DIR = PYTHON_DIR.parent / "AiService"
failures: list[str] = []


def check(label: str, ok: bool, detail: str = "") -> None:
    print(f"[{'ok' if ok else 'FAIL'}] {label}{(': ' + detail) if detail else ''}")
    if not ok:
        failures.append(label)


check("Python 3.12", sys.version_info[:2] == (3, 12), sys.version.split()[0])

# Only the runtime and the service may be importable: nothing from the build machine.
stage = PYTHON_DIR.parent
foreign = [p for p in sys.path if p and not Path(p).resolve().is_relative_to(stage)]
check("import path stays inside the install", not foreign, ", ".join(foreign))
check("AiService on the import path", any(Path(p).resolve() == SERVICE_DIR for p in sys.path if p))

modules = (
    "app.main", "app.api", "app.chat", "app.llm", "app.guided", "tools", "rag.db", "rag.config",
    "ingest.pipeline", "ingest.ocr", "ingest.seed", "recommender", "vision.inspect", "voice.transcribe",
    "litellm", "keyring", "cryptography", "pypdf", "pypdfium2", "numpy", "yaml", "uvicorn", "fastapi",
)
for name in modules:
    try:
        __import__(name)
        check(f"import {name}", True)
    except Exception as exc:  # noqa: BLE001
        check(f"import {name}", False, f"{type(exc).__name__}: {exc}")

try:
    from app.prompts import PROMPTS_DIR, load_prompt

    check("prompts folder", PROMPTS_DIR.resolve() == (SERVICE_DIR / "prompts"), str(PROMPTS_DIR))
    for prompt in ("system.md", "vision.md", "asr.md"):
        try:
            check(f"prompt {prompt}", bool(load_prompt(prompt).strip()))
        except Exception as exc:  # noqa: BLE001
            check(f"prompt {prompt}", False, f"{type(exc).__name__}: {exc}")
except Exception as exc:  # noqa: BLE001
    check("app.prompts", False, f"{type(exc).__name__}: {exc}")

try:
    import sqlite_vec

    conn = sqlite3.connect(":memory:")
    conn.enable_load_extension(True)
    sqlite_vec.load(conn)
    check("sqlite-vec loads", True, conn.execute("select vec_version()").fetchone()[0])
    conn.close()
except Exception as exc:  # noqa: BLE001
    check("sqlite-vec loads", False, f"{type(exc).__name__}: {exc}")

try:
    from app import bootstrap

    check("migrations from AiService\\migrations",
          bootstrap.DEFAULT_MIGRATIONS_DIR.resolve() == (SERVICE_DIR / "migrations"), str(bootstrap.DEFAULT_MIGRATIONS_DIR))
    with tempfile.TemporaryDirectory() as tmp:
        report = bootstrap.ensure_schema(Path(tmp) / "check.db")
        check("schema on a new database", report.applied == [v for v, _ in bootstrap.MIGRATIONS],
              f"applied {report.applied}; {'; '.join(report.notices) or 'no notices'}")
except Exception as exc:  # noqa: BLE001
    check("schema on a new database", False, f"{type(exc).__name__}: {exc}")

try:
    from ingest.ocr import make_page_ocr

    ocr, reason = make_page_ocr()
    # A missing OCR language is the tablet's setting, not a packaging fault; bindings must load.
    ok = ocr is not None or (reason or "").startswith("no Windows OCR language")
    check("Windows OCR bindings", ok, reason or "engine ready")
except Exception as exc:  # noqa: BLE001
    check("Windows OCR bindings", False, f"{type(exc).__name__}: {exc}")

env = {k: v for k, v in os.environ.items() if not k.startswith("PYTHON")}
run = subprocess.run([sys.executable, "-m", "app.main", "--help"], capture_output=True, text=True, env=env,
                     cwd=PYTHON_DIR.parent, timeout=120)
check("python -m app.main --help", run.returncode == 0, (run.stderr or "").strip().splitlines()[-1:] and
      (run.stderr or "").strip().splitlines()[-1] or "")

print(f"{len(failures)} problem(s)" if failures else "Embedded Python is ready to ship.")
sys.exit(1 if failures else 0)
