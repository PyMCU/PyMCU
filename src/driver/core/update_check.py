# -----------------------------------------------------------------------------
# PyMCU CLI Driver — non-blocking PyPI update check
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------

from __future__ import annotations

import json
import os
import sys
import urllib.request
import urllib.error
from datetime import datetime, timezone
from pathlib import Path
from packaging.version import Version

_CACHE_FILE = Path.home() / ".pymcu" / "update-check.json"
_TTL_SECONDS = 86_400  # 24 h
_PACKAGES = ["pymcu-compiler", "pymcu-avr", "pymcu-arm"]


def _is_interactive() -> bool:
    return sys.stdout.isatty() and not os.environ.get("CI") and not os.environ.get("PYMCU_NO_UPDATE_CHECK")


def _fetch_latest_pre(package: str, timeout: float = 2.0) -> str | None:
    """Return the newest version string on PyPI (including pre-releases)."""
    try:
        url = f"https://pypi.org/pypi/{package}/json"
        req = urllib.request.Request(url, headers={"User-Agent": "pymcu-update-check/1.0"})
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            data = json.loads(resp.read())
        releases = [v for v in data.get("releases", {}) if data["releases"][v]]
        if not releases:
            return None
        return str(max(Version(v) for v in releases))
    except Exception:
        return None


def _load_cache() -> dict:
    try:
        if _CACHE_FILE.exists():
            return json.loads(_CACHE_FILE.read_text(encoding="utf-8"))
    except Exception:
        pass
    return {}


def _save_cache(data: dict) -> None:
    try:
        _CACHE_FILE.parent.mkdir(parents=True, exist_ok=True)
        _CACHE_FILE.write_text(json.dumps(data, indent=2), encoding="utf-8")
    except Exception:
        pass


def _cache_is_fresh(cache: dict) -> bool:
    ts = cache.get("checked_at")
    if not ts:
        return False
    try:
        age = (datetime.now(timezone.utc) - datetime.fromisoformat(ts)).total_seconds()
        return age < _TTL_SECONDS
    except Exception:
        return False


def get_available_updates(installed: dict[str, str]) -> dict[str, tuple[str, str]]:
    """Return {pkg: (installed, latest)} for packages with a newer version on PyPI.

    Checks at most once per 24 h; subsequent calls within the window use the cache.
    Returns an empty dict on any network failure so the caller is never blocked.
    """
    if not _is_interactive():
        return {}

    cache = _load_cache()
    if not _cache_is_fresh(cache):
        latest_map: dict[str, str] = {}
        for pkg in _PACKAGES:
            v = _fetch_latest_pre(pkg)
            if v:
                latest_map[pkg] = v
        cache = {
            "checked_at": datetime.now(timezone.utc).isoformat(),
            "latest": latest_map,
        }
        _save_cache(cache)

    updates: dict[str, tuple[str, str]] = {}
    for pkg, current_str in installed.items():
        latest_str = cache.get("latest", {}).get(pkg)
        if not latest_str:
            continue
        try:
            if Version(latest_str) > Version(current_str):
                updates[pkg] = (current_str, latest_str)
        except Exception:
            pass
    return updates


def get_installed_pymcu_versions() -> dict[str, str]:
    """Return installed versions of known pymcu packages in the current env."""
    from importlib.metadata import version, PackageNotFoundError
    result: dict[str, str] = {}
    for pkg in _PACKAGES:
        try:
            result[pkg] = version(pkg)
        except PackageNotFoundError:
            pass
    return result
