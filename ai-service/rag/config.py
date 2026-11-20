"""Config loader for the AI service (ADR-008 config.yaml shape; `llama_server` per ADR-012, `routes` per ADR-013).

Precedence: built-in defaults (declared on the dataclasses below) < config.yaml < overrides.
The app passes --port/--token as `overrides` at runtime; config.yaml is the standalone default.
The token is excluded from repr() so it never reaches logs. Route keys are never in config at all: a route only
names its Windows Credential Manager entry (`key_name`), and a config file that holds a secret is refused.
"""

from __future__ import annotations

import dataclasses
import logging
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Mapping

log = logging.getLogger("gse.ai.config")

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
# A config file must never hold a secret (ADR-013 rule 4): keys live in Windows Credential Manager.
_SECRET_KEYS = frozenset({"api_key", "apikey", "key", "token", "secret", "password"})


def _local_url(port: int) -> str:
    return f"http://127.0.0.1:{port}"


@dataclass
class ServiceConfig:
    host: str = "127.0.0.1"  # never 0.0.0.0
    port: int = 8765
    token: str = field(default="", repr=False)


@dataclass
class EmbeddingServerConfig:
    """Second llama-server, started with --embedding (ADR-012; ADR-003 as amended)."""

    model: str = ""  # embedding GGUF path; empty = attach to a llama-server already listening on `port`
    port: int = 8082
    pooling: str = "mean"  # must match the embedding model (nomic-embed-text v1.5: mean)
    ctx: int = 2048
    extra_args: list = field(default_factory=list)

    @property
    def base_url(self) -> str:
        return _local_url(self.port)


@dataclass
class LlamaServerConfig:
    """llama.cpp `llama-server` processes the AI service starts and supervises (ADR-012). Paths only."""

    binary: str = ""  # llama-server.exe; empty = do not spawn, attach to servers already listening (dev/tests)
    model: str = ""  # chat GGUF path
    mmproj: str = ""  # optional projector GGUF (image/audio input)
    port: int = 8081
    ctx: int = 8192
    threads: int = 0  # 0 = llama.cpp default
    extra_args: list = field(default_factory=list)  # release-specific flags, e.g. ["--reasoning", "off"]
    start_timeout_s: int = 120  # how long a chat waits for a cold model load before answering ai_starting
    embedding: EmbeddingServerConfig = field(default_factory=EmbeddingServerConfig)

    @property
    def base_url(self) -> str:
        return _local_url(self.port)

    @property
    def spawns(self) -> bool:
        return bool(self.binary.strip())


@dataclass
class RemoteRouteConfig:
    """A remote LiteLLM route (ADR-013). No key here: `key_name` names a Windows Credential Manager entry."""

    enabled: bool = False
    model: str = ""  # LiteLLM model string, e.g. "litellm_proxy/<model>" (shop) or "<provider>/<model>" (cloud)
    api_base: str = ""
    key_name: str = ""
    probe_host: str = ""  # cloud only: "host:port" for the online check (TCP connect, nothing sent)

    @property
    def configured(self) -> bool:
        return self.enabled and bool(self.model.strip())


@dataclass
class RoutesConfig:
    shop_server: RemoteRouteConfig = field(default_factory=RemoteRouteConfig)
    cloud: RemoteRouteConfig = field(default_factory=RemoteRouteConfig)
    health_ttl_s: int = 15
    health_timeout_s: float = 1.5


@dataclass
class ModelsConfig:
    chat: str = "lfm3-omni"  # label (llama-server --alias); config-only, never hardcoded in code
    embedding: str = "nomic-embed-text"
    embedding_dim: int = VEC0_DIM


@dataclass
class DegradationConfig:
    offline_chat_message: str = (
        "AI assistant is unavailable (the local model is not running). Manual search remains available."
    )
    fallback_search: str = "bm25"


@dataclass
class Config:
    service: ServiceConfig = field(default_factory=ServiceConfig)
    llama_server: LlamaServerConfig = field(default_factory=LlamaServerConfig)
    models: ModelsConfig = field(default_factory=ModelsConfig)
    routes: RoutesConfig = field(default_factory=RoutesConfig)
    idle_unload_minutes: int = 10
    degradation: DegradationConfig = field(default_factory=DegradationConfig)

    def validate(self) -> None:
        if not isinstance(self.service.port, int) or not (1 <= self.service.port <= 65535):
            raise ValueError("service.port must be an integer in 1..65535")
        if self.service.host not in _LOOPBACK_HOSTS:
            raise ValueError("service.host must be a loopback address (127.0.0.1); never 0.0.0.0")
        ls = self.llama_server
        for where, port in (("llama_server.port", ls.port), ("llama_server.embedding.port", ls.embedding.port)):
            if not isinstance(port, int) or not (1 <= port <= 65535):
                raise ValueError(f"{where} must be an integer in 1..65535")
        if ls.spawns and ls.embedding.model.strip() and ls.port == ls.embedding.port:
            raise ValueError("llama_server.port and llama_server.embedding.port must differ")
        for where, args in (
            ("llama_server.extra_args", ls.extra_args),
            ("llama_server.embedding.extra_args", ls.embedding.extra_args),
        ):
            if not all(isinstance(a, str) for a in args):
                raise ValueError(f"{where} must be a list of strings")
            if any(a.strip().split("=")[0] in {"--host", "--port", "-m", "--model"} for a in args):
                raise ValueError(f"{where} must not set --host/--port/--model: the service sets them (127.0.0.1 only)")
        if ls.start_timeout_s < 1:
            raise ValueError("llama_server.start_timeout_s must be >= 1")
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
        if self.routes.health_ttl_s < 0 or self.routes.health_timeout_s <= 0:
            raise ValueError("routes.health_ttl_s must be >= 0 and routes.health_timeout_s > 0")


_SECTIONS = {
    "service": ServiceConfig,
    "llama_server": LlamaServerConfig,
    "models": ModelsConfig,
    "routes": RoutesConfig,
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
    if isinstance(current, float):
        try:
            return float(value)
        except (TypeError, ValueError) as exc:
            raise ValueError(f"{where} must be a number") from exc
    if isinstance(current, str):
        return "" if value is None else str(value)
    if isinstance(current, list):
        if value is None:
            return []
        if not isinstance(value, list):
            raise ValueError(f"{where} must be a list")
        return list(value)
    return value


def _set_dotted(cfg: Config, dotted: str, value: Any) -> None:
    parts = dotted.split(".")
    obj: Any = cfg
    for part in parts[:-1]:
        if not hasattr(obj, part):
            raise ValueError(f"unknown config key: {dotted}")
        obj = getattr(obj, part)
    leaf = parts[-1]
    if not hasattr(obj, leaf) or dataclasses.is_dataclass(getattr(obj, leaf)):
        raise ValueError(f"unknown config key: {dotted}")
    setattr(obj, leaf, _coerce(getattr(obj, leaf), value, dotted))


def _merge_section(cfg: Config, prefix: str, values: Mapping[str, Any], source: str) -> None:
    obj: Any = cfg
    for part in prefix.split("."):
        obj = getattr(obj, part)
    for key, value in values.items():
        dotted = f"{prefix}.{key}"
        if prefix.split(".")[0] == "routes" and str(key).lower() in _SECRET_KEYS:
            raise ValueError(
                f"{source}: '{dotted}' looks like a secret. Keys belong in Windows Credential Manager "
                "(set routes.<route>.key_name), never in the config file"
            )
        child = getattr(obj, key) if hasattr(obj, key) else None
        if dataclasses.is_dataclass(child):
            if not isinstance(value, Mapping):
                raise ValueError(f"{source}: '{dotted}' must be a mapping")
            _merge_section(cfg, dotted, value, source)
        else:
            _set_dotted(cfg, dotted, value)


def _merge_mapping(cfg: Config, raw: Mapping[str, Any], source: str) -> None:
    for section, values in raw.items():
        if section == "idle_unload_minutes":
            cfg.idle_unload_minutes = _coerce(cfg.idle_unload_minutes, values, f"{source}:{section}")
        elif section == "ollama":
            # Pre-ADR-012 config files still have this block. Ignored (not an error) so an upgraded tablet boots.
            log.warning("%s: the 'ollama' section is obsolete (llama.cpp replaced Ollama, ADR-012); ignored", source)
        elif section in _SECTIONS:
            if not isinstance(values, Mapping):
                raise ValueError(f"{source}: '{section}' must be a mapping")
            _merge_section(cfg, section, values, source)
        else:
            raise ValueError(f"{source}: unknown config section '{section}'")


def load_config(
    path: str | Path | None = None,
    overrides: Mapping[str, Any] | None = None,
) -> Config:
    """Load the effective config.

    `path` (the --config argument) defaults to ai-service/config/config.yaml; a missing default file
    falls back to built-in defaults, but an explicitly given missing file is an error.
    `overrides` accepts aliases ("port", "token", "host") or dotted keys ("llama_server.port").
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
