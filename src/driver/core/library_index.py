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
Builds the curated library index by compiling, not by reading declarations.

The `architectures` field of the Arduino index is filled in by a person and
ages badly.  Here the compiler is already the authority on what builds for what,
so every entry's compatibility is *measured*: each library's example is compiled
for one chip per architecture, and what comes out -- built or not, and how many
bytes -- is what the index publishes.

The author's own `supports.arch` stays in the manifest as a promise, and the two
are compared: a library that declares an architecture it cannot build for, or
builds for one it never declared, is a finding, not a silent discrepancy.
"""

from __future__ import annotations

import json
import shutil
import subprocess
import sys
import tempfile
from dataclasses import dataclass, field
from pathlib import Path

import tomlkit

from .libraries import (
    Library,
    ManifestError,
    discover_libraries,
    read_description,
    site_packages_of,
)

# One chip per architecture, chosen to be the cheapest representative that a
# backend actually supports today.  Measuring every chip would multiply CI time
# for information that barely varies within an architecture; a library that
# cares about a specific chip declares it and gets measured there too.
REPRESENTATIVE_CHIPS: dict[str, str] = {
    "avr": "atmega328p",
    "arm": "rp2040",
    "pic14": "pic16f877a",
}

BUILD_OK = "ok"
BUILD_FAILED = "failed"
BUILD_UNSUPPORTED = "unsupported"


@dataclass
class TargetResult:
    chip: str
    build: str
    flash: int | None = None
    ram: int | None = None
    detail: str = ""

    def to_json(self) -> dict:
        out: dict = {"build": self.build}
        if self.flash is not None:
            out["flash"] = self.flash
        if self.ram is not None:
            out["ram"] = self.ram
        if self.detail:
            out["detail"] = self.detail
        return out


@dataclass
class IndexEntry:
    library: Library
    targets: dict[str, TargetResult] = field(default_factory=dict)
    warnings: list[str] = field(default_factory=list)
    readme: str = ""
    readme_type: str = ""

    def to_json(self, compiler_version: str, generated: str) -> dict:
        lib = self.library
        return {
            "name": lib.name,
            "distribution": lib.distribution,
            "version": lib.version,
            "summary": lib.summary,
            "repository": lib.repository,
            "license": lib.license,
            "categories": lib.categories,
            "provides": lib.modules,
            "readme": self.readme,
            "readme_type": self.readme_type,
            "layer": lib.layer,
            "adapters": lib.adapters,
            "arch": lib.arch,
            "chips": lib.chips,
            "requires": {
                "stdlib": lib.requires_stdlib,
                "compiler": lib.requires_compiler,
                "language_level": lib.language_level,
            },
            "measured": {
                "compiler": compiler_version,
                "date": generated,
                "targets": {chip: result.to_json() for chip, result in sorted(self.targets.items())},
            },
            "status": "broken" if not self.builds_anywhere() else "active",
            "warnings": self.warnings,
        }

    def builds_anywhere(self) -> bool:
        return any(result.build == BUILD_OK for result in self.targets.values())


def chips_to_measure(lib: Library) -> list[str]:
    """
    Which chips to compile this library for.

    Always one per known architecture -- including the ones the library does not
    claim, because "does not build there" is exactly what the index has to be
    able to state -- plus any specific chip the author declared.
    """
    chips = list(REPRESENTATIVE_CHIPS.values())
    for chip in lib.chips:
        if chip not in chips:
            chips.append(chip)
    return chips


def _parse_flash(build_output: str) -> tuple[int | None, int | None]:
    """Pull the flash figure out of a build's own report."""
    flash = None
    for line in build_output.splitlines():
        stripped = " ".join(line.split())
        if stripped.startswith("Flash:"):
            parts = stripped.split()
            if len(parts) > 1 and parts[1].isdigit():
                flash = int(parts[1])
            break
    return flash, None


def measure_example(lib: Library, chip: str, *, pymcu: Path,
                    example: str = "", env_paths: list[str] | None = None) -> TargetResult:
    """
    Compile the library's example for *chip* and report what happened.

    The example is copied and retargeted rather than built in place: it ships
    pinned to whatever board its author used, and the question here is whether
    it builds for this chip.

    *env_paths* is prepended to the child's PYTHONPATH.  Without it the build
    would run against whatever environment the driver itself lives in, and
    measure the absence of the very library it is measuring -- which is exactly
    the first thing this got wrong.
    """
    example_dir = lib.example_dir(example)
    if example_dir is None:
        return TargetResult(chip, BUILD_UNSUPPORTED, detail="no example shipped")

    with tempfile.TemporaryDirectory() as tmp:
        work = Path(tmp) / "example"
        shutil.copytree(example_dir, work)

        config = work / "pyproject.toml"
        if not config.exists():
            return TargetResult(chip, BUILD_UNSUPPORTED,
                                detail="example has no pyproject.toml")

        doc = tomlkit.loads(config.read_text(encoding="utf-8"))
        pymcu_cfg = doc.setdefault("tool", tomlkit.table()).setdefault("pymcu", tomlkit.table())
        pymcu_cfg.pop("board", None)
        pymcu_cfg["target"] = chip
        if lib.layer != "native":
            arr = tomlkit.array()
            arr.append(lib.layer)
            pymcu_cfg["stdlib"] = arr
        config.write_text(tomlkit.dumps(doc), encoding="utf-8")

        import os

        env = dict(os.environ)
        # Measure the code, not the manifest: without this the build would skip
        # the library for any architecture it does not declare, and the result
        # would only ever echo what the author wrote.
        env["PYMCU_LIBRARY_FILTER"] = "0"
        if env_paths:
            existing = env.get("PYTHONPATH", "")
            env["PYTHONPATH"] = os.pathsep.join(
                [*env_paths, existing] if existing else list(env_paths)
            )

        result = subprocess.run([str(pymcu), "build"], cwd=work,
                                capture_output=True, text=True, env=env)
        if result.returncode != 0:
            detail = (result.stderr or result.stdout or "").strip().splitlines()
            return TargetResult(chip, BUILD_FAILED,
                                detail=detail[-1][:200] if detail else "build failed")

        flash, ram = _parse_flash(result.stdout)
        return TargetResult(chip, BUILD_OK, flash=flash, ram=ram)


def compare_with_manifest(lib: Library, targets: dict[str, TargetResult]) -> list[str]:
    """
    Where the author's promise and the measurement disagree.

    Both directions matter: a declared architecture that does not build is a
    broken promise, and an undeclared one that does is a library selling itself
    short -- and, more to the point, a `supports.arch` nobody is maintaining.
    """
    warnings: list[str] = []
    arch_of_chip = {chip: arch for arch, chip in REPRESENTATIVE_CHIPS.items()}

    for chip, result in targets.items():
        arch = arch_of_chip.get(chip)
        if arch is None:
            continue
        declared = arch in lib.arch
        built = result.build == BUILD_OK
        if declared and not built:
            warnings.append(
                f"declares {arch} but the example does not build for {chip}"
                + (f": {result.detail}" if result.detail else "")
            )
        elif built and not declared and lib.arch:
            warnings.append(f"builds for {chip} ({arch}) without declaring it")

    return warnings


def build_entry(lib: Library, *, pymcu: Path,
                env_paths: list[str] | None = None) -> IndexEntry:
    """Measure one library across the chips that apply to it."""
    entry = IndexEntry(library=lib)
    entry.readme, entry.readme_type = read_description(lib.distribution, env_paths)
    for chip in chips_to_measure(lib):
        entry.targets[chip] = measure_example(lib, chip, pymcu=pymcu, env_paths=env_paths)
    entry.warnings = compare_with_manifest(lib, entry.targets)
    return entry


def build_index(venv: Path, *, pymcu: Path, compiler_version: str,
                generated: str) -> tuple[dict, list[str]]:
    """
    Build the whole index from the libraries installed in *venv*.

    Returns (index, problems).  Problems are per-package failures -- an invalid
    manifest, a package that registers no entry point -- reported rather than
    raised, so one bad submission cannot stop the regeneration of the rest.
    """
    search = site_packages_of(venv)
    libraries, problems = discover_libraries(search_path=search or None)

    entries = [build_entry(lib, pymcu=pymcu, env_paths=search) for lib in libraries]
    index = {
        "v": 1,
        "generated": generated,
        "compiler": compiler_version,
        "libraries": [entry.to_json(compiler_version, generated) for entry in entries],
    }
    return index, problems


def write_index(index: dict, path: Path) -> None:
    """Write the index deterministically, so an unchanged run is an empty diff."""
    path.write_text(json.dumps(index, indent=2, sort_keys=False) + "\n", encoding="utf-8")
