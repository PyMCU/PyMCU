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
Distributions absent from the index use that metadata as an unverified
fallback, except for the explicit host-only exclusions below.
"""

from __future__ import annotations

import ast
import json
import os
import re
import shutil
from dataclasses import dataclass
from importlib.metadata import Distribution, distributions
from pathlib import Path

from .libraries import LAYERS, read_cached_library_index


EXCLUDED_DISTRIBUTIONS = frozenset({
    "adafruit-blinka",
    "adafruit-platformdetect",
    "adafruit-pureio",
    "pyftdi",
    "binho-host-adapter",
    "sysv-ipc",
    "rpi-gpio",
    "rpi-ws281x",
    "typing-extensions",
    "circuitpython-stubs",
})


@dataclass(frozen=True)
class UpstreamEntry:
    """One `kind: "upstream"` row of a (fetched or cached) library index."""

    name: str
    distribution: str
    version: str
    provides: tuple[str, ...]
    layer: str = "native"
    repository: str = ""


@dataclass(frozen=True)
class FallbackDistribution:
    """An installed, unindexed distribution and its importable modules."""

    name: str
    version: str
    modules: tuple[str, ...]
    metadata: Distribution


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
    return re.sub(r"[-_.]+", "-", name.strip().lower())


def _is_excluded_distribution(name: str) -> bool:
    normalized = _normalize(name)
    return (normalized in EXCLUDED_DISTRIBUTIONS
            or (normalized.startswith("micropython-")
                and normalized.endswith("-stubs")))


def top_level_modules(dist: Distribution) -> tuple[str, ...]:
    """Importable top-level Python names declared by an installed wheel."""
    declared = dist.read_text("top_level.txt")
    if declared:
        names = {
            line.strip().split(".", 1)[0]
            for line in declared.splitlines()
            if line.strip() and line.strip().split(".", 1)[0].isidentifier()
        }
        if names:
            return tuple(sorted(names))

    names: set[str] = set()
    for entry in dist.files or ():
        parts = entry.parts
        if not parts:
            continue
        top = parts[0]
        if top.endswith((".dist-info", ".egg-info", ".data")):
            continue
        if len(parts) == 1 and top.endswith(".py"):
            module = Path(top).stem
            if module.isidentifier():
                names.add(module)
        elif top.isidentifier() and str(entry).endswith(".py"):
            names.add(top)
    return tuple(sorted(names))


def _installed_distributions(search_path: list[str] | None) -> list[Distribution]:
    try:
        found = distributions(path=search_path) if search_path else distributions()
        return list(found)
    except Exception:
        return []


def excluded_module_names(found: list[Distribution]) -> set[str]:
    """Modules installed by excluded host-only distributions."""
    excluded: set[str] = set()
    for dist in found:
        name = (dist.metadata["Name"] if dist.metadata else "") or ""
        if _is_excluded_distribution(name):
            excluded.update(top_level_modules(dist))
    return excluded


def discover_fallback_distributions(
    entries: list[UpstreamEntry], search_path: list[str] | None,
    ignored_distributions: set[str] | None = None,
) -> list[FallbackDistribution]:
    """Installed distributions absent from the index and safe to expose."""
    found = _installed_distributions(search_path)
    excluded_modules = excluded_module_names(found)
    ignored = {_normalize(name) for name in (ignored_distributions or set())}
    ignored.update(_normalize(entry.distribution) for entry in entries)

    fallback: list[FallbackDistribution] = []
    for dist in found:
        name = (dist.metadata["Name"] if dist.metadata else "") or ""
        normalized = _normalize(name)
        if not name or normalized in ignored or _is_excluded_distribution(name):
            continue
        modules = tuple(
            module for module in top_level_modules(dist)
            if module not in excluded_modules
        )
        if modules:
            fallback.append(FallbackDistribution(
                name=name, version=dist.version or "unknown",
                modules=modules, metadata=dist,
            ))
    return sorted(fallback, key=lambda item: _normalize(item.name))


def find_distribution(distribution: str, search_path: list[str] | None) -> Distribution | None:
    """The installed `Distribution` matching *distribution*, or None."""
    wanted = _normalize(distribution)
    try:
        found = _installed_distributions(search_path)
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


def _stage_modules(dist: Distribution, distribution: str, modules: tuple[str, ...],
                   stage_root: Path) -> Path | None:
    target = stage_root / distribution
    if target.exists():
        shutil.rmtree(target)

    found_any = False
    for module in modules:
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
    return _stage_modules(dist, entry.distribution, entry.provides, stage_root)


def _module_sources(module: str, roots: list[Path]) -> list[tuple[Path, str, bool]]:
    """The package initializers and leaf file the compiler will load."""
    parts = module.split(".")
    for root in roots:
        leaf_file = root.joinpath(*parts).with_suffix(".py")
        leaf_init = root.joinpath(*parts, "__init__.py")
        leaf = leaf_file if leaf_file.is_file() else leaf_init if leaf_init.is_file() else None
        if leaf is None:
            continue
        sources: list[tuple[Path, str, bool]] = []
        for length in range(1, len(parts)):
            init = root.joinpath(*parts[:length], "__init__.py")
            if init.is_file():
                sources.append((init, ".".join(parts[:length]), True))
        sources.append((leaf, module, leaf.name == "__init__.py"))
        return sources
    return []


def _imports(path: Path, module: str, is_package: bool) -> set[str]:
    try:
        tree = ast.parse(path.read_text(encoding="utf-8"), filename=str(path))
    except (OSError, SyntaxError, UnicodeError):
        return set()

    imported: set[str] = set()
    package = module if is_package else module.rpartition(".")[0]
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            imported.update(alias.name for alias in node.names)
            continue
        if not isinstance(node, ast.ImportFrom):
            continue
        if node.level:
            base = package.split(".") if package else []
            keep = max(0, len(base) - node.level + 1)
            prefix = base[:keep]
            if node.module:
                prefix.extend(node.module.split("."))
            name = ".".join(prefix)
        else:
            name = node.module or ""
        if name:
            imported.add(name)
            imported.update(
                f"{name}.{alias.name}" for alias in node.names
                if alias.name != "*"
            )
    return imported


def stage_imported_fallback(
    *, entry_point: Path, roots: list[str], fallback: list[FallbackDistribution],
    stage_root: Path,
) -> tuple[list[str], list[str]]:
    """Stage reachable fallback modules and return includes and warned dists."""
    resolution_roots = [Path(root) for root in roots]
    by_module: dict[str, FallbackDistribution] = {}
    for item in fallback:
        for module in item.modules:
            by_module.setdefault(module, item)

    queue: list[tuple[Path, str, bool]] = [(entry_point, "__main__", False)]
    seen_sources: set[Path] = set()
    staged: dict[str, Path] = {}
    warned: set[str] = set()

    while queue:
        source, module, is_package = queue.pop(0)
        try:
            source_key = source.resolve()
        except OSError:
            source_key = source
        if source_key in seen_sources:
            continue
        seen_sources.add(source_key)

        for imported in sorted(_imports(source, module, is_package)):
            sources = _module_sources(imported, resolution_roots)
            if not sources and "." in imported:
                parent = imported.rpartition(".")[0]
                if _module_sources(parent, resolution_roots):
                    # ``from package import Name`` may name an ordinary
                    # attribute rather than a submodule. Once the package
                    # itself resolved from an earlier root, a missing child
                    # must not pull in another distribution that happens to
                    # own the same top-level package.
                    continue
            top = imported.split(".", 1)[0]
            owner = by_module.get(top)
            if not sources and owner is not None:
                key = _normalize(owner.name)
                target = staged.get(key)
                if target is None:
                    target = _stage_modules(
                        owner.metadata, owner.name, owner.modules, stage_root
                    )
                    if target is None:
                        continue
                    staged[key] = target
                    resolution_roots.append(target)
                warned.add(owner.name)
                sources = _module_sources(imported, resolution_roots)
            queue.extend(sources)

    includes = [str(path) for _, path in sorted(staged.items())]
    return includes, sorted(warned, key=_normalize)


def resolve_upstream_for_target(*, search_path: list[str] | None, flavors: list[str],
                                stage_root: Path,
                                index: dict | None = None,
                                enforce: bool = True,
                                entry_point: Path | None = None,
                                earlier_roots: list[str] | None = None,
                                ignored_distributions: set[str] | None = None,
                                ) -> tuple[list[str], list[str], list[str], list[str]]:
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
    index = _current_index() if index is None else index
    entries = upstream_entries(index)

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

    warned: list[str] = []
    if entry_point is not None:
        fallback = discover_fallback_distributions(
            entries, search_path, ignored_distributions
        )
        fallback_includes, warned = stage_imported_fallback(
            entry_point=entry_point,
            roots=[*(earlier_roots or []), *includes],
            fallback=fallback,
            stage_root=stage_root,
        )
        includes.extend(fallback_includes)
    return includes, skipped, errors, warned


def _current_index() -> dict:
    override = os.environ.get("PYMCU_UPSTREAM_INDEX")
    if override:
        try:
            return json.loads(Path(override).read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return {}
    return read_cached_library_index()
