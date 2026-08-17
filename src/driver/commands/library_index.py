# -----------------------------------------------------------------------------
# PyMCU CLI Driver -- `pymcu index`
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
pymcu index -- build the curated library index.

Run by the CI of the pymcu-libraries repository, both when a submission is
merged and on a schedule.  The scheduled run is the point: it re-measures every
listed library against the current compiler, so an index entry reflects what
builds today rather than what built the day it was submitted.

Sub-commands:
  build   -- install the listed libraries and measure them into index.json
  verify  -- measure what is already installed, and fail on any discrepancy
"""

from __future__ import annotations

import json
import shutil
import subprocess
import sys
from datetime import datetime, timezone
from importlib.metadata import (
    PackageNotFoundError,
    distributions as installed_distributions,
    version as dist_version,
)
from pathlib import Path
from typing import Optional

import typer
from rich.console import Console

from ..core.library_index import (
    BUILD_OK,
    build_index,
    write_index,
)

console = Console()

index_app = typer.Typer(
    name="index",
    help="Build the curated PyMCU library index.",
    no_args_is_help=True,
    hidden=True,
)


def _compiler_version(venv: Path | None = None) -> str:
    """
    The version of the compiler that did the measuring.

    When the measurement ran inside *venv*, that is the version to record --
    not this process's own. The first real index said it had been measured
    with 0.1.0a3, the editable install the generator happened to be running
    from, while every figure in it came from the a6 in the throwaway
    environment.
    """
    if venv is not None:
        from ..core.libraries import site_packages_of

        for site in site_packages_of(venv):
            try:
                found = installed_distributions(path=[site])
                for dist in found:
                    name = ((dist.metadata["Name"] if dist.metadata else "") or "").lower()
                    if name.replace("_", "-") == "pymcu-compiler":
                        return dist.version
            except Exception:
                continue

    try:
        return dist_version("pymcu-compiler")
    except PackageNotFoundError:
        return "unknown"


def _today() -> str:
    return datetime.now(timezone.utc).date().isoformat()


def _read_list(path: Path) -> list[str]:
    """One distribution per line; blank lines and # comments ignored."""
    names: list[str] = []
    for raw in path.read_text(encoding="utf-8").splitlines():
        line = raw.split("#", 1)[0].strip()
        if line:
            names.append(line)
    return names


def _prepare_venv(distributions: list[str], venv: Path, *, pre: bool) -> bool:
    """Create a throwaway environment holding every listed library."""
    uv = shutil.which("uv")
    if uv is None:
        console.print("[red]uv is required to build the index.[/red]")
        return False

    if venv.exists():
        shutil.rmtree(venv)
    if subprocess.run([uv, "venv", str(venv)], capture_output=True).returncode != 0:
        console.print(f"[red]Could not create {venv}.[/red]")
        return False

    ok = True
    # Every backend, not just the one this machine happens to have. The index
    # states which architectures a library builds for, and a missing backend
    # makes that compile fail for a reason that has nothing to do with the
    # library -- publishing it as "does not build on rp2040" would be a
    # measurement of our own environment.
    for distribution in ["pymcu-compiler[all]", *distributions]:
        cmd = [uv, "pip", "install", "--python", str(venv), distribution]
        if pre:
            cmd.append("--prerelease=allow")
        result = subprocess.run(cmd, capture_output=True, text=True)
        if result.returncode != 0:
            # One unresolvable submission must not stop the regeneration of the
            # rest -- it simply does not make it into this run's index.
            console.print(f"[yellow]Could not install {distribution}; skipping it.[/yellow]")
            ok = False
    return ok or True


def _pymcu_executable() -> Path | None:
    candidate = Path(sys.executable).parent / ("pymcu.exe" if sys.platform == "win32" else "pymcu")
    if candidate.exists():
        return candidate
    found = shutil.which("pymcu")
    return Path(found) if found else None


@index_app.command("build")
def index_build(
    libraries_file: Optional[Path] = typer.Option(
        None, "--from", help="File listing one distribution per line."),
    venv: Path = typer.Option(
        Path(".index-venv"), "--venv",
        help="Environment to measure. Created from --from when that is given."),
    output: Path = typer.Option(Path("index.json"), "--output", "-o"),
    pre: bool = typer.Option(True, "--pre/--no-pre", help="Allow pre-release versions."),
    strict: bool = typer.Option(
        False, "--strict",
        help="Exit non-zero when a library's measurement contradicts its manifest."),
):
    """Install the listed libraries, compile their examples, and write index.json."""
    pymcu = _pymcu_executable()
    if pymcu is None:
        console.print("[red]The pymcu executable is not on PATH.[/red]")
        raise typer.Exit(code=1)

    if libraries_file is not None:
        if not libraries_file.exists():
            console.print(f"[red]{libraries_file} not found.[/red]")
            raise typer.Exit(code=1)
        distributions = _read_list(libraries_file)
        console.print(f"[bold]Preparing[/bold] {len(distributions)} libraries in {venv} ...")
        _prepare_venv(distributions, venv, pre=pre)

    if not venv.exists():
        console.print(f"[red]{venv} does not exist. Pass --from to create it.[/red]")
        raise typer.Exit(code=1)

    # Measure with the compiler inside the environment being measured, when it
    # has one: that is the install carrying every backend, and using the
    # outside one meant the matrix reflected whichever extras this machine
    # happened to have.
    # Absolute: each build runs with its cwd inside a temporary copy of the
    # example, so a relative path here resolves against that directory and the
    # compiler is simply not found.
    inner = (venv / ("Scripts" if sys.platform == "win32" else "bin") / (
        "pymcu.exe" if sys.platform == "win32" else "pymcu")).resolve()
    if inner.exists():
        pymcu = inner

    console.print("[bold]Measuring[/bold] (compiling each example per architecture) ...")
    index, problems = build_index(
        venv, pymcu=pymcu, compiler_version=_compiler_version(venv), generated=_today()
    )

    for problem in problems:
        console.print(f"[bold red]Invalid package[/bold red] {problem}")

    write_index(index, output)

    entries = index["libraries"]
    built = sum(
        1 for entry in entries
        if any(t.get("build") == BUILD_OK for t in entry["measured"]["targets"].values())
    )
    flagged = [entry for entry in entries if entry["warnings"]]

    console.print(
        f"[bold green]{output}[/bold green]: {len(entries)} libraries, "
        f"{built} building somewhere, {len(flagged)} with warnings."
    )
    for entry in flagged:
        console.print(f"  [yellow]{entry['name']}[/yellow]: {'; '.join(entry['warnings'])}")

    if strict and (flagged or problems):
        raise typer.Exit(code=1)


@index_app.command("verify")
def index_verify(
    venv: Path = typer.Option(Path(".venv"), "--venv", help="Environment to measure."),
    as_json: bool = typer.Option(False, "--json", help="Emit the measurement as JSON."),
):
    """Measure the installed libraries without writing an index. Fails on any warning."""
    pymcu = _pymcu_executable()
    if pymcu is None:
        console.print("[red]The pymcu executable is not on PATH.[/red]")
        raise typer.Exit(code=1)

    index, problems = build_index(
        venv, pymcu=pymcu, compiler_version=_compiler_version(venv), generated=_today()
    )

    if as_json:
        print(json.dumps({"index": index, "problems": problems}))
    else:
        for problem in problems:
            console.print(f"[bold red]Invalid package[/bold red] {problem}")
        for entry in index["libraries"]:
            targets = entry["measured"]["targets"]
            summary = ", ".join(
                f"{chip}={result['build']}" for chip, result in sorted(targets.items())
            )
            console.print(f"[bold]{entry['name']}[/bold] {entry['version']}: {summary}")
            for warning in entry["warnings"]:
                console.print(f"  [yellow]{warning}[/yellow]")

    failed = problems or any(entry["warnings"] for entry in index["libraries"])
    raise typer.Exit(code=1 if failed else 0)
