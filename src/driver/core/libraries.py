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
Third-party library discovery and compatibility checks.

A PyMCU library is a wheel of Python *sources* that the compiler reads at build
time.  Libraries are discovered through the ``pymcu.libraries`` entry-point
group, exactly like backends and toolchains, and describe themselves in a
``pymcu.toml`` manifest shipped inside the importable package::

    [project.entry-points."pymcu.libraries"]
    dht11 = "pymcu_lib_dht11"

The manifest deliberately carries no version number: the version always comes
from the distribution metadata, so the two can never drift apart.

Everything here is read-only and import-free: the manifest is parsed as data and
the library's own sources are never imported by CPython (they target the MCU and
may use compiler-only constructs).
"""

from __future__ import annotations

import importlib.util
import json
import re
import sys
import tomllib
from urllib.parse import urlparse
from urllib.request import url2pathname as url_to_path
from dataclasses import dataclass, field
from importlib.metadata import (
    PackageNotFoundError,
    distributions,
    entry_points,
    version as dist_version,
)
from pathlib import Path

ENTRY_POINT_GROUP = "pymcu.libraries"
MANIFEST_NAME = "pymcu.toml"

# Bumped when the compiler stops accepting syntax a previous level allowed.
# A library declaring a higher level than this driver understands is refused
# during resolution instead of failing halfway through a build.
LANGUAGE_LEVEL = 1

# Layers a library can be written against.  "native" means pymcu.hal.* and is
# the only one that works regardless of the flavors the project declares.
LAYERS = ("native", "micropython", "circuitpython")


@dataclass
class Library:
    """An installed library: its manifest, plus where it lives on disk."""

    name: str
    distribution: str
    version: str
    package_dir: Path
    summary: str = ""
    license: str = ""
    repository: str = ""
    categories: list[str] = field(default_factory=list)
    modules: list[str] = field(default_factory=list)
    arch: list[str] = field(default_factory=list)
    chips: list[str] = field(default_factory=list)
    layer: str = "native"
    adapters: list[str] = field(default_factory=list)
    symbols: list[str] = field(default_factory=list)
    requires_stdlib: str = ""
    requires_compiler: str = ""
    language_level: int = 1
    examples: dict[str, str] = field(default_factory=dict)

    def adapter_dir(self, flavor: str) -> Path | None:
        """Directory of the compat adapter for *flavor*, if the library ships one."""
        candidate = self.package_dir / "compat" / flavor
        return candidate if candidate.is_dir() else None

    def example_dir(self, name: str = "") -> Path | None:
        """Directory of a declared example, resolved relative to the distribution root."""
        if not self.examples:
            return None
        rel = self.examples.get(name) if name else next(iter(self.examples.values()))
        if not rel:
            return None
        # examples/ sits beside src/<package>/, i.e. two levels above the
        # package directory in the canonical layout.  Installed wheels usually
        # do not ship it, hence the existence check.
        for base in (self.package_dir, self.package_dir.parent, self.package_dir.parent.parent):
            candidate = (base / rel).resolve()
            if candidate.is_dir():
                return candidate
        return None


class ManifestError(Exception):
    """Raised when a pymcu.toml is missing, unreadable or malformed."""


def parse_manifest(manifest_path: Path, *, distribution: str, version: str,
                   package_dir: Path) -> Library:
    """Parse a pymcu.toml into a Library. Raises ManifestError on any problem."""
    try:
        data = tomllib.loads(manifest_path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        raise ManifestError(f"{manifest_path} not found")
    except (tomllib.TOMLDecodeError, UnicodeDecodeError) as exc:
        raise ManifestError(f"{manifest_path}: {exc}")

    lib = data.get("library")
    if not isinstance(lib, dict):
        raise ManifestError(f"{manifest_path}: missing [library] table")

    name = lib.get("name")
    if not name:
        raise ManifestError(f"{manifest_path}: [library] name is required")

    # A version here would be a second source of truth for something the
    # distribution metadata already states.  Refusing it is what keeps the two
    # from disagreeing -- the failure mode this whole design is built around.
    if "version" in lib:
        raise ManifestError(
            f"{manifest_path}: [library] must not declare 'version'. "
            "The version comes from the distribution metadata."
        )

    provides = lib.get("provides", {})
    supports = lib.get("supports", {})
    requires = lib.get("requires", {})

    layer = str(supports.get("layer", "native"))
    if layer not in LAYERS:
        raise ManifestError(
            f"{manifest_path}: unknown layer '{layer}' (expected one of {', '.join(LAYERS)})"
        )

    modules = [str(m) for m in provides.get("modules", [])]
    if not modules:
        raise ManifestError(f"{manifest_path}: [library.provides] modules is required")

    return Library(
        name=str(name),
        distribution=distribution,
        version=version,
        package_dir=package_dir,
        summary=str(lib.get("summary", "")),
        license=str(lib.get("license", "")),
        repository=str(lib.get("repository", "")),
        categories=[str(c) for c in lib.get("categories", [])],
        modules=modules,
        arch=[str(a).lower() for a in supports.get("arch", [])],
        chips=[str(c).lower() for c in supports.get("chips", [])],
        layer=layer,
        adapters=[str(a) for a in supports.get("adapters", [])],
        symbols=[str(s) for s in supports.get("symbols", [])],
        requires_stdlib=str(requires.get("stdlib", "")),
        requires_compiler=str(requires.get("compiler", "")),
        language_level=int(requires.get("language-level", 1)),
        examples={str(k): str(v) for k, v in lib.get("examples", {}).items()},
    )


def load_library(module_name: str, *, distribution: str = "") -> Library:
    """Load the library whose importable package is *module_name*."""
    spec = importlib.util.find_spec(module_name)
    if spec is None or not spec.submodule_search_locations:
        raise ManifestError(f"package '{module_name}' is not installed in this environment")

    package_dir = Path(list(spec.submodule_search_locations)[0])
    dist = distribution or module_name.replace("_", "-")
    try:
        ver = dist_version(dist)
    except PackageNotFoundError:
        ver = "unknown"

    return parse_manifest(
        package_dir / MANIFEST_NAME,
        distribution=dist,
        version=ver,
        package_dir=package_dir,
    )


def site_packages_of(venv: Path) -> list[str]:
    """Return the site-packages directories of a virtualenv, newest layout first."""
    if sys.platform == "win32":
        candidate = venv / "Lib" / "site-packages"
        return [str(candidate)] if candidate.is_dir() else []
    return [str(p) for p in sorted((venv / "lib").glob("python*/site-packages")) if p.is_dir()]


def _editable_project_dir(dist) -> Path | None:
    """
    Where an editable install actually keeps its sources, or None.

    A PEP 660 install leaves nothing in site-packages but a path hook, so the
    directory scan below finds nothing -- which is precisely the layout a
    library author works in while developing one.  direct_url.json records the
    project it points at.
    """
    try:
        raw = dist.read_text("direct_url.json")
        if not raw:
            return None
        info = json.loads(raw)
        if not info.get("dir_info", {}).get("editable"):
            return None
        url = str(info.get("url", ""))
        if not url.startswith("file://"):
            return None
        return Path(url_to_path(urlparse(url).path))
    except Exception:
        return None


def _find_package_dir(top: str, search_path: list[str], dist=None) -> Path | None:
    """Locate an importable package by directory, without importing anything."""
    for base in search_path:
        candidate = Path(base) / top
        if candidate.is_dir():
            return candidate

    project = _editable_project_dir(dist) if dist is not None else None
    if project is not None:
        for candidate in (project / "src" / top, project / top):
            if candidate.is_dir():
                return candidate

    return None


def _load_from_path(module_name: str, search_path: list[str], distribution: str,
                    version: str, dist=None) -> Library:
    """Load a library from an explicit search path, bypassing sys.path."""
    top = module_name.split(".")[0]
    package_dir = _find_package_dir(top, search_path, dist)
    if package_dir is None:
        raise ManifestError(f"package '{module_name}' not found in {', '.join(search_path)}")

    return parse_manifest(
        package_dir / MANIFEST_NAME,
        distribution=distribution,
        version=version,
        package_dir=package_dir,
    )


def discover_libraries(search_path: list[str] | None = None) -> tuple[list[Library], list[str]]:
    """
    Return every installed library, plus the problems found while loading them.

    With *search_path* the lookup ignores sys.path entirely and reads the given
    directories instead.  That is what lets a driver running outside a project's
    environment -- the normal case under pipx, and right after installing
    something into a .venv -- still see what the compiler is going to see.

    A broken manifest never raises here: a single bad package must not take the
    build down before it has had the chance to say which package is bad.
    """
    libraries: list[Library] = []
    problems: list[str] = []

    if search_path is None:
        for ep in entry_points(group=ENTRY_POINT_GROUP):
            dist = getattr(getattr(ep, "dist", None), "name", "") or ""
            try:
                libraries.append(load_library(ep.value, distribution=dist))
            except ManifestError as exc:
                problems.append(f"{dist or ep.name}: {exc}")
    else:
        for dist in distributions(path=search_path):
            name = (dist.metadata["Name"] if dist.metadata else "") or ""
            for ep in dist.entry_points:
                if ep.group != ENTRY_POINT_GROUP:
                    continue
                try:
                    libraries.append(
                        _load_from_path(ep.value, search_path, name,
                                        dist.version or "unknown", dist)
                    )
                except ManifestError as exc:
                    problems.append(f"{name or ep.name}: {exc}")

    libraries.sort(key=lambda lib: lib.name)
    return libraries, problems


# ---------------------------------------------------------------------------
# Chip -> architecture
# ---------------------------------------------------------------------------

_DEVICE_INFO_ARCH = re.compile(r"""device_info\((?=[^)]*\barch\s*=\s*["']([a-z0-9_]+)["'])""")
_ARCH_CACHE: dict[str, str] = {}


def chip_arch(chip: str) -> str:
    """
    Return the architecture of *chip* as the compiler sees it, or "".

    Read straight out of the stdlib's chip definition (`device_info(arch=...)`),
    which is the same declaration `__CHIP__.arch` is built from, so the driver
    and the compiler can never disagree about what an architecture is.  The file
    is parsed, never imported: it is MCU source, not host Python.
    """
    key = chip.lower()
    if key in _ARCH_CACHE:
        return _ARCH_CACHE[key]

    arch = ""
    spec = importlib.util.find_spec(f"pymcu.chips.{key}")
    if spec is not None and spec.origin:
        try:
            match = _DEVICE_INFO_ARCH.search(Path(spec.origin).read_text(encoding="utf-8"))
            arch = match.group(1) if match else ""
        except OSError:
            arch = ""

    _ARCH_CACHE[key] = arch
    return arch


# ---------------------------------------------------------------------------
# Compatibility
# ---------------------------------------------------------------------------

def _version_ok(spec: str, package: str) -> tuple[bool, str]:
    """Check an installed package against a PEP 440 specifier."""
    if not spec:
        return True, ""
    try:
        installed = dist_version(package)
    except PackageNotFoundError:
        return False, f"{package} is not installed (requires {spec})"

    try:
        from packaging.specifiers import SpecifierSet
        from packaging.version import Version

        if Version(installed) not in SpecifierSet(spec, prereleases=True):
            return False, f"{package} {installed} does not satisfy {spec}"
    except Exception:
        # An unparseable specifier is the library author's bug, not the user's:
        # say so rather than silently accepting or rejecting the library.
        return False, f"{package}: cannot interpret requirement '{spec}'"
    return True, ""


def check_compatibility(lib: Library, *, chip: str, flavors: list[str]) -> list[str]:
    """
    Return the reasons *lib* cannot be used for this target. Empty means usable.

    Every check answers a question the user would otherwise only get answered
    halfway through a build, or -- worse -- on the bench.
    """
    reasons: list[str] = []
    arch = chip_arch(chip)

    if lib.chips:
        if chip.lower() not in lib.chips:
            reasons.append(
                f"supports chips {', '.join(lib.chips)}; this project targets {chip}"
            )
    elif lib.arch:
        if not arch:
            reasons.append(
                f"cannot determine the architecture of '{chip}' "
                "(is pymcu-stdlib installed?)"
            )
        elif arch not in lib.arch:
            reasons.append(
                f"supports {', '.join(lib.arch)}; this project targets {chip} ({arch})"
            )

    if lib.layer != "native" and lib.layer not in flavors:
        declared = ", ".join(flavors) if flavors else "none"
        reasons.append(
            f"is written against the {lib.layer} layer, but this project declares "
            f"stdlib = [{declared}]"
        )

    if lib.language_level > LANGUAGE_LEVEL:
        reasons.append(
            f"needs language level {lib.language_level}; this compiler driver "
            f"provides {LANGUAGE_LEVEL}"
        )

    for spec, package in ((lib.requires_stdlib, "pymcu-stdlib"),
                          (lib.requires_compiler, "pymcu-compiler")):
        ok, message = _version_ok(spec, package)
        if not ok:
            reasons.append(message)

    return reasons


def find_layer_shadowing(libraries: list[Library], flavor_dirs: dict[str, Path]) -> list[str]:
    """
    Modules a compat layer already provides, which a library also claims.

    The layer wins -- it comes first on the include path so no library can
    hijack `machine` or `digitalio` -- and that is the right call, but silence
    here means someone installs a library and compiles something else entirely.
    Extracting a driver out of a compat package is exactly when this happens.
    """
    warnings: list[str] = []
    for lib in libraries:
        for module in lib.modules:
            for flavor, directory in flavor_dirs.items():
                if (directory / f"{module}.py").exists() or (directory / module).is_dir():
                    warnings.append(
                        f"'{module}' is provided by both the {flavor} layer and "
                        f"{lib.distribution}; the layer wins, so the library's "
                        f"version is not being compiled"
                    )
    return warnings


def find_module_collisions(libraries: list[Library]) -> list[str]:
    """Report top-level module names claimed by more than one library."""
    owners: dict[str, list[str]] = {}
    for lib in libraries:
        for module in lib.modules:
            owners.setdefault(module, []).append(lib.distribution)

    return [
        f"module '{module}' is provided by {' and '.join(dists)}"
        for module, dists in sorted(owners.items())
        if len(dists) > 1
    ]


def include_paths(libraries: list[Library], flavors: list[str]) -> list[str]:
    """
    Include paths contributed by *libraries*, in compiler search order.

    A library's compat adapter goes before the library itself so the adapter
    can shadow the native module under the same name; the caller places the
    whole block after the flavor packages, so no library can hijack `machine`
    or `digitalio`.
    """
    paths: list[str] = []
    for lib in libraries:
        for flavor in flavors:
            adapter = lib.adapter_dir(flavor)
            if adapter is not None:
                paths.append(str(adapter))
        paths.append(str(lib.package_dir))
    return paths


def search_path_for_project(root: Path) -> list[str] | None:
    """
    Where to look for this project's libraries, or None to use sys.path.

    Returns the project .venv's site-packages only when this interpreter is not
    already running from it.  The driver re-execs into the project environment
    when it can, but not always -- a global install with PyMCU missing from the
    .venv is a case the build already warns about, and it must not silently
    lose the project's libraries on top of that.
    """
    venv = root / ".venv"
    if not venv.is_dir():
        return None
    try:
        if Path(sys.prefix).resolve() == venv.resolve():
            return None
    except OSError:
        return None
    paths = site_packages_of(venv)
    return paths or None


def resolve_for_target(chip: str, flavors: list[str],
                       search_path: list[str] | None = None,
                       enforce: bool = True) -> tuple[list[Library], list[str], list[str]]:
    """
    Split the installed libraries into (usable, skipped, errors) for a target.

    `skipped` holds human-readable "name: reason" lines for libraries that are
    installed but not applicable here -- worth showing, never worth failing on.
    `errors` holds hard problems (bad manifests, module collisions).

    With enforce=False nothing is skipped: every installed library goes on the
    include path and the compiler decides.  That is how the index measures
    compatibility -- filtering by the manifest first would only measure the
    manifest, and could never catch a library that builds for an architecture
    it never declared.
    """
    libraries, errors = discover_libraries(search_path=search_path)

    usable: list[Library] = []
    skipped: list[str] = []
    for lib in libraries:
        reasons = check_compatibility(lib, chip=chip, flavors=flavors) if enforce else []
        if reasons:
            skipped.append(f"{lib.name}: {'; '.join(reasons)}")
        else:
            usable.append(lib)

    errors.extend(find_module_collisions(usable))
    return usable, skipped, errors
