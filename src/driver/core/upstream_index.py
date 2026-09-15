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
Measures an upstream library submission the same way a manifest library's
example is measured, into an index.json entry carrying `kind: "upstream"`.

The distribution is installed like any other; what differs is where the
measurement program comes from. A manifest library's example lives inside its
own sdist, at a path the manifest declares. An upstream submission's example
is a single file this index repository commits under its own
`upstream-examples/`, named by `UpstreamSubmission.example` -- a copy of the
library's own example, kept only as the measurement program, never a copy of
the library itself.
"""

from __future__ import annotations

import json
import os
import subprocess
import tempfile
from dataclasses import dataclass, field
from pathlib import Path

import tomlkit

from .libraries import chip_arch
from .library_index import (
    BUILD_FAILED,
    BUILD_OK,
    BUILD_UNMEASURED,
    BUILD_UNSUPPORTED,
    REPRESENTATIVE_CHIPS,
    TargetResult,
    UpstreamSubmission,
    _failure_reason,
    _parse_flash,
)
from .upstream_libraries import find_distribution

# What the driver prints when the backend for a chip is not installed.
# Duplicated from library_index.py's own _MISSING_BACKEND rather than
# imported: a stray reference to a truly private (leading-underscore) name
# from another module is worth avoiding twice, not once.
_MISSING_BACKEND = "pymcu-compiler["

# A layer whose idiomatic example imports the top-level `board` module needs
# an actual board, not a bare chip: `board.py` is generated from a *named*
# board (arduino_uno, raspberry_pi_pico, ...), never from a chip on its own.
# REPRESENTATIVE_CHIPS exists to pick one chip per architecture without a
# manifest's `supports.chips` to read; this is the same idea one layer up,
# for the one board per architecture that layer actually ships.
REPRESENTATIVE_BOARDS: dict[str, dict[str, str]] = {
    "circuitpython": {"avr": "arduino_uno", "arm": "raspberry_pi_pico"},
    "micropython": {"avr": "arduino_uno"},
}


def _representative_board(layer: str, chip: str) -> str | None:
    return REPRESENTATIVE_BOARDS.get(layer, {}).get(chip_arch(chip))


def chips_to_measure_upstream() -> list[str]:
    """One chip per architecture -- an upstream submission declares no chips
    or architectures of its own to add to (there is no manifest to declare
    them in); that is exactly what this measurement exists to find out."""
    return list(REPRESENTATIVE_CHIPS.values())


def _dist_metadata(distribution: str, search_path: list[str] | None
                   ) -> tuple[str, str, str, str]:
    """(summary, license, repository, version) read from installed metadata."""
    dist = find_distribution(distribution, search_path)
    if dist is None:
        return "", "", "", ""

    meta = dist.metadata
    summary = (meta.get("Summary") or "").strip() if meta else ""
    license_ = ((meta.get("License-Expression") or meta.get("License") or "").strip()
               if meta else "")

    repository = ""
    if meta is not None:
        for entry in meta.get_all("Project-URL") or []:
            label, _, url = str(entry).partition(",")
            if label.strip().lower() in ("homepage", "source", "repository", "source code"):
                repository = url.strip()
                break
        if not repository:
            repository = (meta.get("Home-page") or "").strip()

    return summary, license_, repository, (dist.version or "unknown")


@dataclass
class UpstreamIndexEntry:
    """One measured `kind: "upstream"` row, ready to serialize."""

    submission: UpstreamSubmission
    version: str = ""
    summary: str = ""
    license: str = ""
    repository: str = ""
    targets: dict[str, TargetResult] = field(default_factory=dict)

    def builds_anywhere(self) -> bool:
        return any(result.build == BUILD_OK for result in self.targets.values())

    def to_json(self, compiler_version: str, generated: str) -> dict:
        sub = self.submission
        return {
            "kind": "upstream",
            "name": sub.name or sub.provides[0],
            "distribution": sub.distribution,
            "version": self.version,
            "summary": self.summary,
            "repository": self.repository or sub.distribution,
            "license": self.license,
            "provides": list(sub.provides),
            "layer": sub.layer,
            "measured": {
                "compiler": compiler_version,
                "date": generated,
                "targets": {chip: r.to_json() for chip, r in sorted(self.targets.items())},
            },
            "status": "broken" if not self.builds_anywhere() else "active",
            # An upstream submission declares no supports.arch to compare
            # the measurement against -- there is no manifest to hold a
            # promise -- so there is nothing here for compare_with_manifest
            # to disagree with.
            "warnings": [],
        }


def measure_upstream_example(submission: UpstreamSubmission, chip: str, *, pymcu: Path,
                             example_source: Path, version: str = "",
                             env_paths: list[str] | None = None) -> TargetResult:
    """
    Compile *submission*'s committed measurement program for *chip*.

    Builds a throwaway project around the single committed file rather than
    an examples/ directory with its own pyproject.toml -- an upstream
    submission's example is exactly one file, since the whole point is that
    nothing else about the library lives in this repository.

    The measurement subprocess is a plain `pymcu build`, which stages an
    upstream library onto the include path by reading PYMCU_UPSTREAM_INDEX or
    the cached library index (core.upstream_libraries). Neither exists yet
    for a submission being measured for the very first time -- that is
    exactly what this run produces -- so a one-entry index describing just
    this submission is written and pointed to with PYMCU_UPSTREAM_INDEX for
    the duration of this one subprocess call.
    """
    if not example_source.is_file():
        return TargetResult(chip, BUILD_UNSUPPORTED,
                            detail=f"no measurement program at {example_source}")

    with tempfile.TemporaryDirectory() as tmp:
        work = Path(tmp) / "example"
        (work / "src").mkdir(parents=True)
        (work / "src" / "main.py").write_text(
            example_source.read_text(encoding="utf-8"), encoding="utf-8"
        )

        doc = tomlkit.document()
        project_table = tomlkit.table()
        project_table["name"] = "upstream-measurement"
        project_table["version"] = "0.0.0"
        doc["project"] = project_table

        pymcu_cfg = tomlkit.table()
        board = _representative_board(submission.layer, chip)
        if board:
            pymcu_cfg["board"] = board
        else:
            pymcu_cfg["target"] = chip
        pymcu_cfg["sources"] = "src"
        pymcu_cfg["entry"] = "main.py"
        if submission.layer != "native":
            arr = tomlkit.array()
            arr.append(submission.layer)
            pymcu_cfg["stdlib"] = arr
        tool = tomlkit.table()
        tool["pymcu"] = pymcu_cfg
        doc["tool"] = tool
        (work / "pyproject.toml").write_text(tomlkit.dumps(doc), encoding="utf-8")

        upstream_index = Path(tmp) / "upstream-index.json"
        upstream_index.write_text(json.dumps({
            "v": 1,
            "libraries": [{
                "kind": "upstream",
                "name": submission.name or submission.provides[0],
                "distribution": submission.distribution,
                "version": version or "0.0.0",
                "provides": list(submission.provides),
                "layer": submission.layer,
            }],
        }), encoding="utf-8")

        env = dict(os.environ)
        env["PYMCU_UPSTREAM_INDEX"] = str(upstream_index)
        if env_paths:
            existing = env.get("PYTHONPATH", "")
            env["PYTHONPATH"] = os.pathsep.join(
                [*env_paths, existing] if existing else list(env_paths)
            )

        result = subprocess.run([str(pymcu), "build"], cwd=work,
                                capture_output=True, text=True, env=env)
        if result.returncode != 0:
            output = result.stderr or result.stdout or ""
            if _MISSING_BACKEND in output:
                # Our environment, not the distribution's fault: publishing
                # this as `failed` would put a claim about someone else's
                # code on a machine that simply never installed a backend.
                return TargetResult(chip, BUILD_UNMEASURED,
                                    detail=f"backend for {chip} not installed")
            return TargetResult(chip, BUILD_FAILED, detail=_failure_reason(output))

        flash, ram = _parse_flash(result.stdout)
        return TargetResult(chip, BUILD_OK, flash=flash, ram=ram)


def build_upstream_entry(submission: UpstreamSubmission, *, pymcu: Path, repo_root: Path,
                         env_paths: list[str] | None = None
                         ) -> tuple[UpstreamIndexEntry | None, str]:
    """
    Measure one upstream submission across every architecture.

    Returns (entry, problem). A problem -- the distribution is not actually
    installed -- is reported rather than raised, the same way an invalid
    manifest library is: one bad submission must not stop the rest of the
    index from being built.
    """
    summary, license_, repository, version = _dist_metadata(submission.distribution, env_paths)
    if not version:
        return None, f"{submission.distribution}: not installed"

    entry = UpstreamIndexEntry(
        submission=submission, version=version, summary=summary,
        license=license_, repository=repository,
    )

    example_source = (repo_root / submission.example).resolve()
    for chip in chips_to_measure_upstream():
        entry.targets[chip] = measure_upstream_example(
            submission, chip, pymcu=pymcu, example_source=example_source,
            version=version, env_paths=env_paths,
        )

    return entry, ""
