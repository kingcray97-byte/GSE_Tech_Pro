"""Config loader for the AI service (ADR-008 config.yaml shape).

Precedence: built-in defaults (declared on the dataclasses below) < config.yaml < overrides.
The app passes --port/--token as `overrides` at runtime; config.yaml is the standalone default.
The token is excluded from repr() so it never reaches logs.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Mapping

PACKAGE_ROOT = Path(__file__).resolve().parent.parent  # ai-service/
# Phase-8 location is ai-service/config/config.yaml; until it ships, ai-service/app/config.yaml is used.
_CONFIG_CANDIDATES = (PACKAGE_ROOT / "config" / "config.yaml", PACKAGE_ROOT / "app" / "config.yaml")


def default_config_path() -> Path:
    """First existing default config file; when neither exists, the app/ location (defaults apply)."""
    return next((p for p in _CONFIG_CANDIDATES if p.exists()), _CONFIG_CANDIDATES[1])


DEFAULT_CONFIG_PATH = default_config_path()

# Schema-fixed: vec0 `embeddings.embedding float[768]` (migration 003, ADR-003).
VEC0_DIM = 768

_LOOPBACK_HOSTS = frozenset({"127.0.0.1", "localhost", "::1"})
_ALIASES = {"port": "service.port", "token": "service.token", "host": "service.host"}


@dataclass
class ServiceConfig:
    host: str = "127.0.0.1"  # never 0.0.0.0
    port: int = 8765
    token: str = field(default="", repr=False)


@dataclass
class OllamaConfig:
    host: str = "127.0.0.1:11434"
    no_cloud: bool = True

    @property
    def base_url(self) -> str:
        host = self.host.strip().rstrip("/")
        return host if "://" in host else f"http://{host}"


@dataclass
class ModelsConfig:
    chat: str = "lfm3-omni"  # config-only; never hardcoded in code
    embedding: str = "nomic-embed-text"
    embedding_dim: int = VEC0_DIM


@dataclass
class DegradationConfig:
    offline_chat_message: str = (
        "AI assistant is unavailable (Ollama not reachable). Manual search remains available."
    )
    fallback_search: str = "bm25"


@dataclass
class Config:
    service: ServiceConfig = field(default_factory=ServiceConfig)
    ollama: OllamaConfig = field(default_factory=OllamaConfig)
    models: ModelsConfig = field(default_factory=ModelsConfig)
    idle_unload_minutes: int = 10
    degradation: DegradationConfig = field(default_factory=DegradationConfig)

    def validate(self) -> None:
        if not isinstance(self.service.port, int) or not (1 <= self.service.port <= 65535):
            raise ValueError("service.port must be an integer in 1..65535")
        if self.service.host not in _LOOPBACK_HOSTS:
            raise ValueError("service.host must be a loopback address (127.0.0.1); never 0.0.0.0")
        if not self.ollama.host.strip():
            raise ValueError("ollama.host must be set")
        if not self.models.chat.strip():
            raise ValueError("models.chat must be set")
        if not self.models.embedding.strip():
            raise ValueError("models.embedding must be set")
        if self.models.embedding_dim != VEC0_DIM:
            raise ValueError(
                f"models.embedding_dim must be {VEC0_DIM} (vec0 schema is fixed); "
                "changing it requires re-embedding every chunk"
            )
        if self.idle_unload_minutes < 0:
            raise ValueError("idle_unload_minutes must be >= 0")


_SECTIONS = {
    "service": ServiceConfig,
    "ollama": OllamaConfig,
    "models": ModelsConfig,
    "degradation": DegradationConfig,
}


def _coerce(current: Any, value: Any, where: str) -> Any:
    if isinstance(current, bool):
        if isinstance(value, bool):
            return value
        if isinstance(value, str) and value.lower() in {"true", "false"}:
            return value.lower() == "true"
        raise ValueError(f"{where} must be a boolean")
    if isinstance(current, int):
        try:
            return int(value)
        except (TypeError, ValueError) as exc:
            raise ValueError(f"{where} must be an integer") from exc
    if isinstance(current, str):
        return str(value)
    return value


def _set_dotted(cfg: Config, dotted: str, value: Any) -> None:
    parts = dotted.split(".")
    obj: Any = cfg
    for part in parts[:-1]:
        if not hasattr(obj, part):
            raise ValueError(f"unknown config key: {dotted}")
        obj = getattr(obj, part)
    leaf = parts[-1]
    if not hasattr(obj, leaf):
        raise ValueError(f"unknown config key: {dotted}")
    setattr(obj, leaf, _coerce(getattr(obj, leaf), value, dotted))


def _merge_mapping(cfg: Config, raw: Mapping[str, Any], source: str) -> None:
    for section, values in raw.items():
        if section == "idle_unload_minutes":
            cfg.idle_unload_minutes = _coerce(cfg.idle_unload_minutes, values, f"{source}:{section}")
        elif section in _SECTIONS:
            if not isinstance(values, Mapping):
                raise ValueError(f"{source}: '{section}' must be a mapping")
            for key, value in values.items():
                _set_dotted(cfg, f"{section}.{key}", value)
        else:
            raise ValueError(f"{source}: unknown config section '{section}'")


def load_config(
    path: str | Path | None = None,
    overrides: Mapping[str, Any] | None = None,
) -> Config:
    """Load the effective config.

    `path` (the --config argument) defaults to ai-service/config/config.yaml; a missing default file
    falls back to built-in defaults, but an explicitly given missing file is an error.
    `overrides` accepts aliases ("port", "token", "host") or dotted keys ("ollama.host").
    `None` override values are ignored, so argparse defaults can be passed straight through.
    """
    cfg = Config()
    explicit = path is not None
    cfg_path = Path(path) if explicit else default_config_path()
    if cfg_path.exists():
        import yaml  # PyYAML; listed in the service requirements

        raw = yaml.safe_load(cfg_path.read_text(encoding="utf-8")) or {}
        if not isinstance(raw, Mapping):
            raise ValueError(f"{cfg_path}: top level must be a mapping")
        _merge_mapping(cfg, raw, str(cfg_path))
    elif explicit:
        raise FileNotFoundError(f"config file not found: {cfg_path}")

    for key, value in (overrides or {}).items():
        if value is None:
            continue
        _set_dotted(cfg, _ALIASES.get(key, key), value)

    cfg.validate()
    return cfg
