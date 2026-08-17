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
    read_example,
    site_packages_of,
    ssl_context,
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

PYPI_JSON = "https://pypi.org/pypi/{distribution}/{version}/json"

BUILD_OK = "ok"
BUILD_FAILED = "failed"
BUILD_UNSUPPORTED = "unsupported"

# The build never ran, so nothing was learned about the library. Distinct from
# "failed" on purpose: a missing backend, or an index host that cannot be
# reached, says something about the machine doing the measuring, and
# publishing it as "does not build here" would put a claim about someone
# else's library on a fault of our own.
BUILD_UNMEASURED = "unmeasured"

# What the driver prints when the backend for a chip is not installed.
_MISSING_BACKEND = "pymcu-compiler["


def _claims_chip(lib: "Library", chip: str) -> bool:
    """Whether the library's own manifest covers this chip.

    A library that declares nothing claims everything, so an unqualified
    manifest keeps getting `failed` -- silence is not a get-out.
    """
    from .libraries import chip_arch  # noqa: PLC0415

    if lib.chips:
        return chip.lower() in lib.chips
    if lib.arch:
        return chip_arch(chip) in lib.arch
    return True


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
    example: dict = field(default_factory=dict)

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
            "example": self.example,
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
                    example: str = "", env_paths: list[str] | None = None,
                    example_dir: Path | None = None) -> TargetResult:
    """
    Compile the library's example for *chip* and report what happened.

    The example is copied and retargeted rather than built in place: it ships
    pinned to whatever board its author used, and the question here is whether
    it builds for this chip.

    *example_dir* is the already-resolved source, which the caller fetches once
    per library rather than once per chip -- it usually comes out of the sdist,
    since examples do not travel in the wheel.

    *env_paths* is prepended to the child's PYTHONPATH.  Without it the build
    would run against whatever environment the driver itself lives in, and
    measure the absence of the very library it is measuring -- which is exactly
    the first thing this got wrong.
    """
    if example_dir is None:
        example_dir = lib.example_dir(example)
    if example_dir is None:
        return TargetResult(chip, BUILD_UNSUPPORTED, detail="no example available")

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
            output = (result.stderr or result.stdout or "")
            message = _failure_reason(output)
            if _MISSING_BACKEND in output:
                # Our environment, not their library.
                return TargetResult(chip, BUILD_UNMEASURED,
                                    detail=f"backend for {chip} not installed")
            # Outside what the author declared, a failure is not a defect: it is
            # the library being used where it never claimed to work. Reporting
            # `failed` there reads as "this library is broken", which is an
            # accusation the manifest already answered. The build still runs --
            # the point of PYMCU_LIBRARY_FILTER=0 is that code beating an
            # over-cautious manifest is worth publishing as `ok` -- but when it
            # does not build, the honest label is `unsupported`.
            if not _claims_chip(lib, chip):
                return TargetResult(chip, BUILD_UNSUPPORTED, detail=message)
            return TargetResult(chip, BUILD_FAILED, detail=message)

        flash, ram = _parse_flash(result.stdout)
        return TargetResult(chip, BUILD_OK, flash=flash, ram=ram)


def fetch_sdist(distribution: str, version: str, dest: Path) -> tuple[Path | None, str]:
    """
    Unpack the distribution's sdist into *dest*, returning its root.

    Examples do not ship in the wheel -- they belong with the tests and the
    docs, at the distribution root -- so the installed package cannot answer
    "what does this library's example compile to". The sdist can: it is an
    immutable, versioned artefact that already carries everything needed to
    build and check the project, which is what it exists for. Returns None if
    the distribution publishes no sdist or the download fails; the caller
    reports that rather than treating it as a build failure.
    """
    import urllib.error
    import urllib.request

    url = PYPI_JSON.format(distribution=distribution, version=version)
    try:
        context = ssl_context()
        opener = (urllib.request.urlopen(url, timeout=30, context=context)
                  if context else urllib.request.urlopen(url, timeout=30))
        with opener as response:
            metadata = json.loads(response.read().decode("utf-8"))
    except (urllib.error.URLError, OSError, ValueError) as exc:
        return None, f"could not reach PyPI: {exc}"

    archive_url = next(
        (f["url"] for f in metadata.get("urls", [])
         if f.get("packagetype") == "sdist"),
        None,
    )
    if archive_url is None:
        return None, f"{distribution} {version} publishes no sdist"

    archive = dest / "sdist.tar.gz"
    try:
        context = ssl_context()
        opener = (urllib.request.urlopen(archive_url, timeout=60, context=context)
                  if context else urllib.request.urlopen(archive_url, timeout=60))
        with opener as response:
            archive.write_bytes(response.read())
    except (urllib.error.URLError, OSError) as exc:
        return None, f"could not download the sdist: {exc}"

    import tarfile

    unpacked = dest / "unpacked"
    try:
        with tarfile.open(archive) as tar:
            # filter="data" refuses absolute paths, parent traversal and
            # anything that is not a plain file or directory: this is an
            # archive from the network, unpacked by CI.
            tar.extractall(unpacked, filter="data")
    except (tarfile.TarError, OSError) as exc:
        return None, f"could not unpack the sdist: {exc}"

    roots = [p for p in unpacked.iterdir() if p.is_dir()]
    return (roots[0] if len(roots) == 1 else unpacked), "sdist"


def example_source(lib: Library, name: str, workdir: Path) -> tuple[Path | None, str]:
    """
    Where to build a library's example from, and how it was found.

    A source checkout has them on disk; an installed wheel does not, and the
    sdist is fetched instead.
    """
    on_disk = lib.example_dir(name)
    if on_disk is not None:
        return on_disk, "checkout"

    if not lib.examples:
        return None, "none declared"

    rel = lib.examples.get(name) if name else next(iter(lib.examples.values()))
    if not rel:
        return None, "none declared"

    root, reason = fetch_sdist(lib.distribution, lib.version, workdir)
    if root is None:
        return None, reason

    candidate = root / rel
    return (candidate, "sdist") if candidate.is_dir() else (None, f"sdist has no {rel}")


def _failure_reason(output: str) -> str:
    """
    The line worth publishing out of a failed build's output.

    The last line is usually the path of the temporary directory the example
    was copied into, which tells a reader nothing -- the first index recorded
    "/private/var/folders/.../tmpg0ohl5wd/e" as the reason two libraries did
    not build. The diagnostic itself is the line that says so.
    """
    lines = [line.strip() for line in output.strip().splitlines() if line.strip()]
    for line in lines:
        marker = line.find("error:")
        if marker != -1:
            # From the marker on, not the whole line: a diagnostic starts with
            # the path of the temporary directory the example was copied into,
            # which is both meaningless to a reader and long enough to fill the
            # 200-character budget on its own -- the first index published
            # "/private/var/folders/.../tmph5jb45kc/exampl" as a reason.
            return line[marker + len("error:"):].strip()[:200]
    return lines[-1][:200] if lines else "build failed"


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
        if result.build == BUILD_UNMEASURED:
            # Nothing was learned here, so there is nothing to hold against
            # the author: telling someone their manifest is wrong on the
            # strength of a build that never ran is worse than staying quiet.
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

    with tempfile.TemporaryDirectory() as tmp:
        # Resolved once, not once per chip: this may pull the sdist over the
        # network, and doing that per target would download the same archive
        # three times to compile three copies of one file.
        source, how = example_source(lib, "", Path(tmp))
        if source is None:
            entry.warnings.append(f"no example measured: {how}")
        else:
            entry.example = read_example(lib, directory=source)
            for chip in chips_to_measure(lib):
                entry.targets[chip] = measure_example(
                    lib, chip, pymcu=pymcu, env_paths=env_paths, example_dir=source
                )

    entry.warnings.extend(compare_with_manifest(lib, entry.targets))
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
