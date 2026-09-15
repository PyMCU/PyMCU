# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
#
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

"""
Upstream libraries: a third-party PyPI distribution the index only measures
and vouches for -- no PyMCU manifest, no wrapper package, and never a copy of
its code.

A manifest library (core/libraries.py) describes itself: a `pymcu.toml` and a
`pymcu.libraries` entry point say what it provides. An upstream distribution,
by definition, ships neither -- an ``index.json`` entry with ``kind:
"upstream"`` supplies that missing metadata instead (which module(s), which
stdlib layer). This module turns such an entry, plus a distribution actually
installed in a project's environment, into an include-path directory the
compiler can read.

Discovery is entirely import-free, like core.libraries: everything is read
from `importlib.metadata`, never by importing the distribution's own code.
"""

from __future__ import annotations

import shutil
from dataclasses import dataclass
from importlib.metadata import Distribution, distributions
from pathlib import Path

from .libraries import LAYERS, read_cached_library_index


@dataclass(frozen=True)
class UpstreamEntry:
    """One `kind: "upstream"` row of a (fetched or cached) library index."""

    name: str
    distribution: str
    version: str
    provides: tuple[str, ...]
    layer: str = "native"
    repository: str = ""


def upstream_entries(index: dict) -> list[UpstreamEntry]:
    """Every upstream-kind entry in *index*, ignoring anything malformed."""
    entries: list[UpstreamEntry] = []
    if not isinstance(index, dict):
        return entries
    for raw in index.get("libraries", []):
        if not isinstance(raw, dict) or raw.get("kind") != "upstream":
            continue
        distribution = str(raw.get("distribution", "")).strip()
        provides = tuple(str(m) for m in raw.get("provides", []) if str(m))
        if not distribution or not provides:
            continue
        layer = str(raw.get("layer", "native"))
        entries.append(UpstreamEntry(
            name=str(raw.get("name", "")) or distribution,
            distribution=distribution,
            version=str(raw.get("version", "")),
            provides=provides,
            layer=layer if layer in LAYERS else "native",
            repository=str(raw.get("repository", "")),
        ))
    return entries


def _normalize(name: str) -> str:
    return name.strip().lower().replace("_", "-")


def find_distribution(distribution: str, search_path: list[str] | None) -> Distribution | None:
    """The installed `Distribution` matching *distribution*, or None."""
    wanted = _normalize(distribution)
    try:
        found = distributions(path=search_path) if search_path else distributions()
    except Exception:
        return None
    for dist in found:
        name = (dist.metadata["Name"] if dist.metadata else "") or ""
        if _normalize(name) == wanted:
            return dist
    return None


def installed_distribution_version(distribution: str, search_path: list[str] | None) -> str | None:
    """The installed version of *distribution* here, or None if not installed."""
    dist = find_distribution(distribution, search_path)
    return (dist.version or "unknown") if dist is not None else None


def discover_installed_upstream(entries: list[UpstreamEntry],
                                search_path: list[str] | None) -> list[UpstreamEntry]:
    """
    Which of *entries* are actually installed here, with the installed version.

    The index's own `version` is what was verified when it was last measured;
    the one reported here is what a build actually has on disk, which can be
    newer (or older, with --pre) than that.
    """
    installed: list[UpstreamEntry] = []
    for entry in entries:
        dist = find_distribution(entry.distribution, search_path)
        if dist is None:
            continue
        version = dist.version or "unknown"
        installed.append(entry if version == entry.version else
                         UpstreamEntry(entry.name, entry.distribution, version,
                                       entry.provides, entry.layer, entry.repository))
    return installed


def _module_path(dist: Distribution, module: str) -> Path | None:
    """
    The file or directory of one top-level module of *dist*, from its RECORD.

    Never imports the module. A single-file module (``adafruit_hcsr04.py``)
    and a package (a directory with an ``__init__.py``) are both declared the
    same way in ``provides`` and both handled here.
    """
    single = f"{module}.py"
    saw_package = False
    for entry in dist.files or []:
        parts = entry.parts
        if not parts:
            continue
        if len(parts) == 1 and parts[0] == single:
            return Path(entry.locate())
        if parts[0] == module:
            saw_package = True
    if saw_package:
        candidate = Path(dist.locate_file(module))
        if candidate.is_dir():
            return candidate
    return None


def stage_modules(entry: UpstreamEntry, search_path: list[str] | None,
                  stage_root: Path) -> Path | None:
    """
    Copy *entry*'s declared modules into ``stage_root/<distribution>/`` and
    return that directory, or None if none of them could be found installed.

    Staged rather than pointed at directly. The file for a top-level module
    sits in site-packages next to every other installed distribution -- an
    unrelated one, another library, an editable checkout of this very driver
    -- so putting that directory on the include path would let a firmware
    resolve `import` to anything installed alongside it. That is exactly the
    hazard `Library.source_dir` (never `package_dir`) exists to avoid for a
    manifest library; copying only the files this entry declares gives an
    upstream one, which ships no manifest to draw that line itself, the same
    guarantee. A pointer at the module's own directory was the alternative
    (no copy, symlink-cheap) but only works when the module IS its own
    directory -- a single-file module like `adafruit_hcsr04.py` has no
    directory of its own to point at without exposing its site-packages
    siblings too.
    """
    dist = find_distribution(entry.distribution, search_path)
    if dist is None:
        return None

    target = stage_root / entry.distribution
    if target.exists():
        shutil.rmtree(target)

    found_any = False
    for module in entry.provides:
        source = _module_path(dist, module)
        if source is None:
            continue
        target.mkdir(parents=True, exist_ok=True)
        if source.is_dir():
            shutil.copytree(source, target / source.name, dirs_exist_ok=True)
        else:
            shutil.copy2(source, target / source.name)
        found_any = True
    return target if found_any else None


def resolve_upstream_for_target(*, search_path: list[str] | None, flavors: list[str],
                                stage_root: Path,
                                enforce: bool = True) -> tuple[list[str], list[str], list[str]]:
    """
    Include paths, skip notes and errors for the upstream libraries in play.

    Reads only the *cached* index (core.libraries.read_cached_library_index):
    a build never touches the network, so an upstream library only appears on
    the include path once `pymcu install` or `pymcu search` has fetched the
    index it is listed in at least once. `pymcu index build` overrides this
    with PYMCU_UPSTREAM_INDEX, since a fresh index is exactly what it is
    generating and has not published anywhere yet.

    With enforce=False (PYMCU_LIBRARY_FILTER=0) every installed upstream
    entry is staged regardless of its declared layer, mirroring what that
    flag already does for manifest libraries: the index measures compatibility
    by compiling, not by trusting the declaration.
    """
    index = _current_index()
    entries = upstream_entries(index)
    if not entries:
        return [], [], []

    includes: list[str] = []
    skipped: list[str] = []
    errors: list[str] = []
    for entry in discover_installed_upstream(entries, search_path):
        if enforce and entry.layer != "native" and entry.layer not in flavors:
            declared = ", ".join(flavors) if flavors else "none"
            skipped.append(
                f"{entry.distribution}: is written against the {entry.layer} layer, "
                f"but this project declares stdlib = [{declared}]"
            )
            continue
        staged = stage_modules(entry, search_path, stage_root)
        if staged is None:
            errors.append(
                f"{entry.distribution}: could not find {', '.join(entry.provides)} "
                "among its installed files"
            )
            continue
        includes.append(str(staged))
    return includes, skipped, errors


def _current_index() -> dict:
    import json
    import os

    override = os.environ.get("PYMCU_UPSTREAM_INDEX")
    if override:
        try:
            return json.loads(Path(override).read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return {}
    return read_cached_library_index()
