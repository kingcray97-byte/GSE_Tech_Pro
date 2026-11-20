"""AI routing: tablet, shop server, cloud (ADR-013).

- `plan_routes` is the one route order. `src/GseTechPro.Core/Ai/AiRouting.cs` (`AiRouting.Plan`) must agree; both
  have tests pinning the same table.
- `RouteHealth` caches short health checks for the two remote routes. A remote route whose last check failed is
  skipped without waiting. The tablet route is checked by app.runtime (llama-server /health).
- Keys come from Windows Credential Manager through `keyring` (service KEYRING_SERVICE, user = route.key_name).
  Keys are never logged, never returned, never written to config.
"""

from __future__ import annotations

import logging
import socket
import threading
import time
from dataclasses import dataclass
from typing import Callable, Literal, Optional
from urllib.parse import urlparse

import httpx

from rag.config import Config, RemoteRouteConfig

log = logging.getLogger("gse.ai.routing")

RouteName = Literal["tablet", "shop-server", "cloud"]
RoutingMode = Literal["tablet-only", "prefer-shop-server", "prefer-cloud"]
TABLET: RouteName = "tablet"
SHOP: RouteName = "shop-server"
CLOUD: RouteName = "cloud"
KEYRING_SERVICE = "GSE Tech Pro AI"

# Tools that read job data (asset, repair/part history, DTCs for an asset, readings). Without "share job details",
# the cloud route gets only manual search (ADR-013 rule 3).
CLOUD_SAFE_TOOLS = frozenset({"search_manuals"})


def plan_routes(mode: str, cloud_allowed: bool, shop_configured: bool, cloud_enabled_in_service: bool) -> list[str]:
    """Routes to try, in order. The tablet is always last; the cloud needs both keys (service config + app admin)."""
    plan: list[str] = []
    if mode == "prefer-cloud" and cloud_allowed and cloud_enabled_in_service:
        plan.append(CLOUD)
    if mode in ("prefer-cloud", "prefer-shop-server") and shop_configured:
        plan.append(SHOP)
    plan.append(TABLET)
    return plan


# ------------------------------------------------------------------ secrets

SecretLookup = Callable[[str], Optional[str]]


def keyring_lookup(key_name: str) -> Optional[str]:
    """Read one key from Windows Credential Manager. None when unset, keyring missing, or the backend fails."""
    if not key_name.strip():
        return None
    try:
        import keyring  # lazy: not needed for the tablet-only default

        value = keyring.get_password(KEYRING_SERVICE, key_name)
    except Exception:  # keyring missing or backend error: the route is just not usable
        log.warning("credential lookup failed for a configured route (key not read)")
        return None
    return value or None


# ------------------------------------------------------------------ health


@dataclass(frozen=True)
class RouteStatus:
    route: str
    configured: bool
    available: bool
    model: Optional[str]
    message: str


def shop_probe(route: RemoteRouteConfig, timeout_s: float) -> tuple[bool, str]:
    """LiteLLM proxy liveness (`GET /health/liveliness`, no auth needed)."""
    base = route.api_base.strip().rstrip("/")
    if not base:
        return False, "No proxy address"
    try:
        resp = httpx.get(f"{base}/health/liveliness", timeout=timeout_s)
    except httpx.HTTPError:
        return False, "Not reachable"
    return (True, "Online") if resp.status_code == 200 else (False, f"Proxy answered {resp.status_code}")


def _probe_target(route: RemoteRouteConfig) -> Optional[tuple[str, int]]:
    if route.probe_host.strip():
        host, _, port = route.probe_host.strip().rpartition(":")
        if host and port.isdigit():
            return host.strip("[]"), int(port)
        return route.probe_host.strip(), 443
    if route.api_base.strip():
        parsed = urlparse(route.api_base.strip())
        if parsed.hostname:
            return parsed.hostname, parsed.port or (443 if parsed.scheme == "https" else 80)
    return None


def cloud_probe(route: RemoteRouteConfig, timeout_s: float) -> tuple[bool, str]:
    """Online check: a TCP connect to the provider host. Nothing is sent."""
    target = _probe_target(route)
    if target is None:
        return False, "No probe_host or api_base to check"
    try:
        with socket.create_connection(target, timeout=timeout_s):
            return True, "Online"
    except OSError:
        return False, "Offline"


RemoteProbe = Callable[[RemoteRouteConfig, float], tuple[bool, str]]


class RouteHealth:
    """Cached health of the remote routes. `mark_down` after a failed call skips the route until the next check."""

    def __init__(
        self,
        config: Config,
        *,
        secrets: SecretLookup = keyring_lookup,
        shop_probe_fn: RemoteProbe = shop_probe,
        cloud_probe_fn: RemoteProbe = cloud_probe,
        clock: Callable[[], float] = time.monotonic,
    ) -> None:
        self._config = config
        self._secrets = secrets
        self._probes: dict[str, RemoteProbe] = {SHOP: shop_probe_fn, CLOUD: cloud_probe_fn}
        self._clock = clock
        self._lock = threading.Lock()
        self._cache: dict[str, tuple[float, bool, str]] = {}

    def route_config(self, route: str) -> RemoteRouteConfig:
        return self._config.routes.shop_server if route == SHOP else self._config.routes.cloud

    def key_for(self, route: str) -> Optional[str]:
        return self._secrets(self.route_config(route).key_name)

    def check(self, route: str, *, fresh: bool = False) -> tuple[bool, str]:
        cfg = self.route_config(route)
        if not cfg.configured:
            return False, "Not set up"
        if cfg.key_name.strip() and self.key_for(route) is None:
            return False, "Key not in Credential Manager"
        if route == CLOUD and not cfg.key_name.strip():
            return False, "No key_name configured"
        ttl = float(self._config.routes.health_ttl_s)
        with self._lock:
            hit = self._cache.get(route)
            if not fresh and hit is not None and self._clock() - hit[0] < ttl:
                return hit[1], hit[2]
        ok, message = self._probes[route](cfg, float(self._config.routes.health_timeout_s))
        with self._lock:
            self._cache[route] = (self._clock(), ok, message)
        return ok, message

    def mark_down(self, route: str, message: str = "Last request failed") -> None:
        with self._lock:
            self._cache[route] = (self._clock(), False, message)

    def status(self, route: str) -> RouteStatus:
        cfg = self.route_config(route)
        ok, message = self.check(route)
        return RouteStatus(route=route, configured=cfg.configured, available=ok,
                           model=cfg.model.strip() or None, message=message)
