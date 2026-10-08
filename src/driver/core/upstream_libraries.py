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
import sys
import tomllib
from dataclasses import dataclass
from importlib.metadata import Distribution, distributions
from pathlib import Path
from packaging.requirements import InvalidRequirement, Requirement
from urllib.parse import unquote, urlparse
from urllib.request import url2pathname

from .libraries import LAYERS, read_cached_library_index


EXCLUDED_DISTRIBUTIONS = frozenset({
    # Python environment and packaging tools are host programs, not firmware
    # libraries. Several of these expose tempting import names (notably
    # setuptools' pkg_resources and distutils) despite existing only to manage
    # the environment in which the compiler driver runs.
    "pip",
    "setuptools",
    "wheel",
    "packaging",
    "virtualenv",
    "pipx",
    "poetry",
    "uv",
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
    "pyserial",
    "pyusb",
    "hidapi",
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


@dataclass(frozen=True)
class VerifiedUpstream:
    """An index entry paired with the exact installed metadata it verified."""

    entry: UpstreamEntry
    metadata: Distribution


class StagingError(Exception):
    """An installed distribution was found but its sources cannot be staged."""


def upstream_entries(index: dict) -> list[UpstreamEntry]:
    """Every upstream-kind entry in *index*, ignoring anything malformed."""
    entries: list[UpstreamEntry] = []
    if not isinstance(index, dict):
        return entries
    libraries = index.get("libraries", [])
    if not isinstance(libraries, list):
        return entries
    for raw in libraries:
        if not isinstance(raw, dict) or raw.get("kind") != "upstream":
            continue
        distribution = raw.get("distribution", "")
        provides = raw.get("provides", [])
        string_fields = (
            raw.get("name", ""), raw.get("version", ""),
            raw.get("layer", "native"), raw.get("repository", ""),
        )
        if (not isinstance(distribution, str) or not distribution.strip()
                or not isinstance(provides, list) or not provides
                or not all(isinstance(module, str) and module for module in provides)
                or not all(isinstance(value, str) for value in string_fields)):
            continue
        distribution = distribution.strip()
        layer = raw.get("layer", "native")
        entries.append(UpstreamEntry(
            name=raw.get("name", "") or distribution,
            distribution=distribution,
            version=raw.get("version", ""),
            provides=tuple(provides),
            layer=layer if layer in LAYERS else "native",
            repository=raw.get("repository", ""),
        ))
    return entries


def _normalize(name: str) -> str:
    return re.sub(r"[-_.]+", "-", name.strip().lower())


def _is_excluded_distribution(name: str) -> bool:
    normalized = _normalize(name)
    return (normalized in EXCLUDED_DISTRIBUTIONS
            or normalized.startswith("types-")
            or normalized.endswith("-stubs"))


def _requires_blinka(dist: Distribution) -> bool:
    """Whether this distribution targets a host through Adafruit-Blinka."""
    for raw in dist.requires or ():
        try:
            if _normalize(Requirement(raw).name) == "adafruit-blinka":
                return True
        except InvalidRequirement:
            continue
    return False


def _declares_mcu_compatibility(dist: Distribution) -> bool:
    """Whether package metadata explicitly names an MCU Python runtime."""
    classifiers = dist.metadata.get_all("Classifier") if dist.metadata else []
    return any(
        "micropython" in classifier.lower()
        or "circuitpython" in classifier.lower()
        or classifier.lower() == "topic :: software development :: embedded systems"
        for classifier in classifiers or ()
    )


def _console_scripts_only(dist: Distribution) -> bool:
    """Whether a distribution advertises commands but no plugin API."""
    entry_points = list(dist.entry_points)
    return bool(entry_points) and all(
        entry.group == "console_scripts" for entry in entry_points
    )


def _pymcu_host_dependencies(found: list[Distribution]) -> set[str]:
    """Direct host dependencies of the compiler driver installed here."""
    dependencies: set[str] = set()
    for dist in found:
        name = (dist.metadata["Name"] if dist.metadata else "") or ""
        if _normalize(name) != "pymcu-compiler":
            continue
        for raw in dist.requires or ():
            try:
                dependencies.add(_normalize(Requirement(raw).name))
            except InvalidRequirement:
                continue
    return dependencies


def _is_host_only_distribution(
    dist: Distribution, pymcu_dependencies: set[str] | None = None,
) -> bool:
    name = (dist.metadata["Name"] if dist.metadata else "") or ""
    return (_is_excluded_distribution(name)
            or _normalize(name) in (pymcu_dependencies or set())
            or (_console_scripts_only(dist)
                and not _declares_mcu_compatibility(dist))
            or (_requires_blinka(dist)
                and not _declares_mcu_compatibility(dist)))


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
    pymcu_dependencies = _pymcu_host_dependencies(found)
    embedded = {
        module
        for dist in found if _declares_mcu_compatibility(dist)
        for module in top_level_modules(dist)
    }
    excluded: set[str] = set()
    for dist in found:
        if _is_host_only_distribution(dist, pymcu_dependencies):
            excluded.update(top_level_modules(dist))
    return excluded - embedded


def discover_fallback_distributions(
    entries: list[UpstreamEntry], search_path: list[str] | None,
    ignored_distributions: set[str] | None = None,
) -> list[FallbackDistribution]:
    """Installed distributions absent from the index and safe to expose."""
    found = _installed_distributions(search_path)
    pymcu_dependencies = _pymcu_host_dependencies(found)
    excluded_modules = excluded_module_names(found)
    ignored = {_normalize(name) for name in (ignored_distributions or set())}
    ignored.update(_normalize(entry.distribution) for entry in entries)

    fallback: list[FallbackDistribution] = []
    for dist in found:
        name = (dist.metadata["Name"] if dist.metadata else "") or ""
        normalized = _normalize(name)
        if (not name or normalized in ignored
                or _is_host_only_distribution(dist, pymcu_dependencies)):
            continue
        modules = tuple(
            module for module in top_level_modules(dist)
            if module not in excluded_modules
            and module not in sys.stdlib_module_names
        )
        if modules:
            fallback.append(FallbackDistribution(
                name=name, version=dist.version or "unknown",
                modules=modules, metadata=dist,
            ))
    # Reachability decides what is actually staged. When two distributions
    # claim one import name, explicit MCU-runtime metadata wins ownership.
    return sorted(
        fallback,
        key=lambda item: (
            not _declares_mcu_compatibility(item.metadata),
            _normalize(item.name),
        ),
    )


def _distribution_sort_key(dist: Distribution) -> str:
    """Stable ordering for duplicate metadata directories."""
    return str(getattr(dist, "_path", ""))


def find_distribution(distribution: str, search_path: list[str] | None, *,
                      version: str | None = None) -> Distribution | None:
    """The installed `Distribution` matching *distribution*, or None."""
    wanted = _normalize(distribution)
    matches = []
    for dist in _installed_distributions(search_path):
        name = (dist.metadata["Name"] if dist.metadata else "") or ""
        if (_normalize(name) == wanted
                and (version is None or (dist.version or "unknown") == version)):
            matches.append(dist)
    return min(matches, key=_distribution_sort_key) if matches else None


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
        dist = find_distribution(
            entry.distribution, search_path, version=entry.version
        ) or find_distribution(entry.distribution, search_path)
        if dist is None:
            continue
        version = dist.version or "unknown"
        installed.append(entry if version == entry.version else
                         UpstreamEntry(entry.name, entry.distribution, version,
                                       entry.provides, entry.layer, entry.repository))
    return installed


def _verified_upstream_entries(entries: list[UpstreamEntry],
                               search_path: list[str] | None) -> list[VerifiedUpstream]:
    """Index entries whose measured version is exactly what is installed."""
    found = _installed_distributions(search_path)
    pymcu_dependencies = _pymcu_host_dependencies(found)
    verified: list[VerifiedUpstream] = []
    for entry in entries:
        matches = [
            dist for dist in found
            if _normalize((dist.metadata["Name"] if dist.metadata else "") or "")
            == _normalize(entry.distribution)
            and (dist.version or "unknown") == entry.version
            and not _is_host_only_distribution(dist, pymcu_dependencies)
        ]
        if matches:
            verified.append(VerifiedUpstream(
                entry, min(matches, key=_distribution_sort_key)
            ))
    return verified


def _recorded_module_files(dist: Distribution, module: str) -> list[tuple[Path, Path]]:
    """
    Files of one top-level module that RECORD attributes to *dist*.

    Returning individual files is important for namespace packages. Two wheels
    may both own files below ``shared/``; copying that directory wholesale
    would silently stage one distribution's code as if the other owned it.
    """
    single = f"{module}.py"
    package = f"{module}/"
    found: list[tuple[Path, Path]] = []
    for entry in dist.files or ():
        parts = entry.parts
        if not parts:
            continue
        relative = Path(*parts)
        if relative.is_absolute() or ".." in relative.parts:
            continue
        text = relative.as_posix()
        if text != single and not text.startswith(package):
            continue
        source = Path(entry.locate())
        if source.is_file():
            found.append((source, relative))
    return found


def _editable_project_root(dist: Distribution) -> Path | None:
    """The local project named by PEP 610/660 metadata, without running .pth."""
    raw = dist.read_text("direct_url.json")
    if not raw:
        return None
    try:
        direct = json.loads(raw)
        if not direct.get("dir_info", {}).get("editable"):
            return None
        parsed = urlparse(str(direct.get("url", "")))
        if parsed.scheme != "file":
            return None
        path = url2pathname(unquote(parsed.path))
        if parsed.netloc:
            path = f"//{parsed.netloc}{path}"
        root = Path(path)
        return root if root.is_dir() else None
    except (AttributeError, json.JSONDecodeError, OSError, TypeError):
        return None


def _editable_source_roots(project_root: Path) -> list[Path]:
    """Declarative source roots used by common PEP 660 build backends."""
    roots = [project_root, project_root / "src"]
    pyproject = project_root / "pyproject.toml"
    try:
        doc = tomllib.loads(pyproject.read_text(encoding="utf-8"))
    except (OSError, tomllib.TOMLDecodeError, UnicodeError):
        doc = {}

    tool = doc.get("tool", {}) if isinstance(doc, dict) else {}
    setuptools = tool.get("setuptools", {}) if isinstance(tool, dict) else {}
    package_dir = setuptools.get("package-dir", {}) if isinstance(setuptools, dict) else {}
    if isinstance(package_dir, dict):
        roots.extend(project_root / str(value) for value in package_dir.values())

    poetry = tool.get("poetry", {}) if isinstance(tool, dict) else {}
    packages = poetry.get("packages", []) if isinstance(poetry, dict) else []
    if isinstance(packages, list):
        for package in packages:
            if isinstance(package, dict) and package.get("from"):
                roots.append(project_root / str(package["from"]))

    hatch = tool.get("hatch", {}) if isinstance(tool, dict) else {}
    build = hatch.get("build", {}) if isinstance(hatch, dict) else {}
    targets = build.get("targets", {}) if isinstance(build, dict) else {}
    wheel = targets.get("wheel", {}) if isinstance(targets, dict) else {}
    hatch_packages = wheel.get("packages", []) if isinstance(wheel, dict) else []
    if isinstance(hatch_packages, list):
        for package in hatch_packages:
            package_path = project_root / str(package)
            roots.append(package_path.parent)

    unique: list[Path] = []
    for root in roots:
        try:
            resolved = root.resolve()
        except OSError:
            resolved = root
        if resolved not in unique:
            unique.append(resolved)
    return unique


def _setuptools_package_dir(project_root: Path, module: str) -> Path | None:
    """Resolve an explicit setuptools package-dir mapping for *module*."""
    try:
        doc = tomllib.loads(
            (project_root / "pyproject.toml").read_text(encoding="utf-8")
        )
    except (OSError, tomllib.TOMLDecodeError, UnicodeError):
        return None
    tool = doc.get("tool", {}) if isinstance(doc, dict) else {}
    setuptools = tool.get("setuptools", {}) if isinstance(tool, dict) else {}
    package_dir = setuptools.get("package-dir", {}) if isinstance(setuptools, dict) else {}
    if not isinstance(package_dir, dict) or module not in package_dir:
        return None
    return project_root / str(package_dir[module])


def _editable_module_files(dist: Distribution, module: str) -> list[tuple[Path, Path]]:
    project_root = _editable_project_root(dist)
    if project_root is None:
        return []
    mapped_package = _setuptools_package_dir(project_root, module)
    if mapped_package is not None:
        if not mapped_package.is_dir() or not (mapped_package / "__init__.py").is_file():
            return []
        destination = Path(*module.split("."))
        return [
            (source, destination / source.relative_to(mapped_package))
            for source in mapped_package.rglob("*") if source.is_file()
        ]
    for root in _editable_source_roots(project_root):
        single = root / f"{module}.py"
        if single.is_file():
            return [(single, Path(single.name))]
        package = root / module
        if package.is_dir() and (package / "__init__.py").is_file():
            return [
                (source, Path(module) / source.relative_to(package))
                for source in package.rglob("*") if source.is_file()
            ]
    return []


def _stage_modules(dist: Distribution, distribution: str, modules: tuple[str, ...],
                   stage_root: Path) -> Path | None:
    target = stage_root / _normalize(distribution)
    if target.exists():
        shutil.rmtree(target)

    found_any = False
    for module in modules:
        files = _recorded_module_files(dist, module)
        if not files:
            files = _editable_module_files(dist, module)
        if not files:
            continue
        for source, relative in files:
            destination = target / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, destination)
        found_any = True
    if not found_any and _editable_project_root(dist) is not None:
        raise StagingError(
            f"{distribution}: editable install source could not be located for "
            f"{', '.join(modules)}"
        )
    return target if found_any else None


def stage_modules(entry: UpstreamEntry, search_path: list[str] | None,
                  stage_root: Path, *, metadata: Distribution | None = None) -> Path | None:
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
    dist = metadata or find_distribution(entry.distribution, search_path)
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


def _regular_package_in_roots(module: str, roots: list[Path]) -> bool:
    """Whether an earlier root owns *module* as a module or regular package."""
    parts = module.split(".")
    return any(
        root.joinpath(*parts).with_suffix(".py").is_file()
        or root.joinpath(*parts, "__init__.py").is_file()
        for root in roots
    )


def provided_module_names(package: Path) -> set[str]:
    """Top-level import names supplied by a stdlib or compatibility package."""
    names: set[str] = set()
    try:
        children = list(package.iterdir())
    except OSError:
        return names
    for child in children:
        if child.is_file() and child.suffix == ".py" and child.stem != "__init__":
            if child.stem.isidentifier():
                names.add(child.stem)
        elif child.is_dir() and child.name.isidentifier():
            try:
                if any(path.suffix == ".py" for path in child.rglob("*.py")):
                    names.add(child.name)
            except OSError:
                continue
    return names


def _stdlib_alias_sources(module: str, roots: list[Path]) -> list[tuple[Path, str, bool]]:
    """Resolve a protected bare name through the compiler's pymcu alias rule."""
    if "." in module:
        return []
    for root in roots:
        sources = _module_sources(f"pymcu.{module}", [root])
        if sources:
            path, _, is_package = sources[-1]
            return [(path, module, is_package)]
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
    stage_root: Path, protected_modules: set[str] | None = None,
) -> tuple[list[str], list[str], list[str]]:
    """Stage reachable fallback modules and return includes, warnings, errors."""
    resolution_roots = [Path(root) for root in roots]
    by_module: dict[str, list[FallbackDistribution]] = {}
    for item in fallback:
        for module in item.modules:
            by_module.setdefault(module, []).append(item)

    def owner_of(imported: str) -> FallbackDistribution | None:
        candidates = by_module.get(imported.split(".", 1)[0], [])
        if len(candidates) < 2 or "." not in imported:
            return candidates[0] if candidates else None
        leaf_file = Path(*imported.split(".")).with_suffix(".py")
        leaf_init = Path(*imported.split("."), "__init__.py")
        for candidate in candidates:
            owned = {
                relative for _, relative in _recorded_module_files(
                    candidate.metadata, imported.split(".", 1)[0]
                )
            }
            if not owned:
                owned = {
                    relative for _, relative in _editable_module_files(
                        candidate.metadata, imported.split(".", 1)[0]
                    )
                }
            if leaf_file in owned or leaf_init in owned:
                return candidate
        return candidates[0]

    queue: list[tuple[Path, str, bool]] = [(entry_point, "__main__", False)]
    seen_sources: set[Path] = set()
    staged: dict[str, Path] = {}
    warned: set[str] = set()
    errors: set[str] = set()
    protected = set(protected_modules or ()) | set(sys.stdlib_module_names)

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
            top = imported.split(".", 1)[0]
            owner = owner_of(imported)
            sources = _module_sources(imported, resolution_roots)
            if top in protected:
                if owner is not None:
                    warned.add(
                        f"{owner.name}: not staging {top} because the PyMCU stdlib "
                        "or active compatibility layer provides that name"
                    )
                if not sources:
                    sources = _stdlib_alias_sources(imported, resolution_roots)
                queue.extend(sources)
                continue
            if not sources and "." in imported:
                parent = imported.rpartition(".")[0]
                if _module_sources(parent, resolution_roots):
                    # ``from package import Name`` may name an ordinary
                    # attribute rather than a submodule. Once the package
                    # itself resolved from an earlier root, a missing child
                    # must not pull in another distribution that happens to
                    # own the same top-level package.
                    continue
            if not sources and owner is not None:
                key = _normalize(owner.name)
                target = staged.get(key)
                if target is None:
                    try:
                        target = _stage_modules(
                            owner.metadata, owner.name, owner.modules, stage_root
                        )
                    except StagingError as exc:
                        errors.add(str(exc))
                        continue
                    if target is None:
                        continue
                    staged[key] = target
                    resolution_roots.append(target)
                warned.add(
                    f"{owner.name} is not in the PyMCU library index: "
                    "compiling it unverified"
                )
                sources = _module_sources(imported, resolution_roots)
            queue.extend(sources)

    includes = [str(path) for _, path in sorted(staged.items())]
    return (includes, sorted(warned, key=_normalize),
            sorted(errors, key=_normalize))


def resolve_upstream_for_target(*, search_path: list[str] | None, flavors: list[str],
                                stage_root: Path,
                                index: dict | None = None,
                                enforce: bool = True,
                                entry_point: Path | None = None,
                                earlier_roots: list[str] | None = None,
                                ignored_distributions: set[str] | None = None,
                                protected_modules: set[str] | None = None,
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
    malformed_index = (
        not isinstance(index, dict)
        or ("libraries" in index and not isinstance(index["libraries"], list))
    )
    malformed_rows = (
        isinstance(index, dict)
        and isinstance(index.get("libraries", []), list)
        and any(
            isinstance(raw, dict) and raw.get("kind") == "upstream"
            and not upstream_entries({"libraries": [raw]})
            for raw in index.get("libraries", [])
        )
    )
    entries = upstream_entries(index)
    verified = _verified_upstream_entries(entries, search_path)
    verified_entries = [item.entry for item in verified]

    includes: list[str] = []
    skipped: list[str] = []
    errors: list[str] = []
    warned: list[str] = []
    if malformed_index:
        warned.append(
            "library index is malformed: ignoring it and discovering imported "
            "distributions unverified"
        )
    elif malformed_rows:
        warned.append(
            "library index contains malformed upstream data: ignoring it and "
            "discovering imported distributions unverified"
        )
    protected = protected_modules or set()
    prior_roots = [Path(root) for root in earlier_roots or ()]
    for installed in verified:
        entry = installed.entry
        if enforce and entry.layer != "native" and entry.layer not in flavors:
            declared = ", ".join(flavors) if flavors else "none"
            skipped.append(
                f"{entry.distribution}: is written against the {entry.layer} layer, "
                f"but this project declares stdlib = [{declared}]"
            )
            continue
        regular_package_collisions = sorted({
            module.split(".", 1)[0] for module in entry.provides
            if _regular_package_in_roots(module.split(".", 1)[0], prior_roots)
        })
        for module in regular_package_collisions:
            warned.append(
                f"{entry.distribution}: not staging {module} because an earlier "
                "include root provides it as a module or regular package"
            )
        collisions = sorted({
            module.split(".", 1)[0] for module in entry.provides
            if module.split(".", 1)[0] in protected
        })
        for module in collisions:
            warned.append(
                f"{entry.distribution}: not staging {module} because the PyMCU "
                "stdlib or active compatibility layer provides that name"
            )
        stage_entry = UpstreamEntry(
            entry.name, entry.distribution, entry.version,
            tuple(module for module in entry.provides
                  if module.split(".", 1)[0] not in protected
                  and module.split(".", 1)[0] not in regular_package_collisions),
            entry.layer, entry.repository,
        )
        if not stage_entry.provides:
            continue
        try:
            staged = stage_modules(
                stage_entry, search_path, stage_root, metadata=installed.metadata
            )
        except StagingError as exc:
            errors.append(str(exc))
            continue
        if staged is None:
            errors.append(
                f"{entry.distribution}: could not find {', '.join(entry.provides)} "
                "among its installed files"
            )
            continue
        includes.append(str(staged))

    if entry_point is not None:
        fallback = discover_fallback_distributions(
            verified_entries, search_path, ignored_distributions
        )
        fallback_includes, fallback_warned, fallback_errors = stage_imported_fallback(
            entry_point=entry_point,
            roots=[*(earlier_roots or []), *includes],
            fallback=fallback,
            stage_root=stage_root,
            protected_modules=protected,
        )
        includes.extend(fallback_includes)
        errors.extend(fallback_errors)
        warned.extend(fallback_warned)
    return includes, skipped, errors, sorted(set(warned), key=_normalize)


def _current_index() -> dict:
    override = os.environ.get("PYMCU_UPSTREAM_INDEX")
    if override:
        try:
            return json.loads(Path(override).read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            return {}
    return read_cached_library_index()
