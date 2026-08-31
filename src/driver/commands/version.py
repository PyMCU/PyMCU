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

import sys
from pathlib import Path
from rich.console import Console
from rich.table import Table
from rich import box

console = Console()

def _discover_companions(already_listed: set[str]) -> list[tuple[str, str]]:
    """Every other installed pymcu distribution, described by what it provides.

    Named by the entry-point groups it registers, so a backend reads as a
    backend without this command knowing which backends exist.
    """
    from importlib.metadata import distributions, entry_points

    _GROUP_LABEL = {
        "pymcu.backends": "Codegen Backend",
        "pymcu.toolchains": "Toolchain",
        "pymcu.programmers": "Programmer",
    }

    provided: dict[str, set[str]] = {}
    for group, label in _GROUP_LABEL.items():
        for ep in entry_points(group=group):
            dist = getattr(ep, "dist", None)
            if dist is not None and dist.name:
                provided.setdefault(_normalize(dist.name), set()).add(label)

    found: dict[str, str] = {}
    for dist in distributions():
        raw = dist.metadata["Name"] if dist.metadata else None
        if not raw:
            continue
        name = _normalize(raw)
        if not name.startswith("pymcu") or name in already_listed or name in found:
            continue
        labels = provided.get(name)
        found[name] = ", ".join(sorted(labels)) if labels else _describe(name)
    return sorted(found.items())


def _describe(name: str) -> str:
    """A fallback for packages that register no entry point.

    Deliberately narrow: a wrong label is worse than none, so anything not
    recognised gets an empty cell rather than being filed under whichever
    category happened to be the default.
    """
    if name.endswith(("-sdk", "-toolchain-sdk")):
        return "SDK"
    if name.endswith("-toolchain"):
        return "Toolchain Binaries"
    if name in ("pymcu-micropython", "pymcu-circuitpython"):
        return "Compatibility Layer"
    return ""


def _normalize(name: str) -> str:
    return name.strip().lower().replace("_", "-")


def _resolved_environment() -> tuple[str, str]:
    """(path, how it was chosen) for the installation these versions come FROM.

    The table used to name no environment at all, and the numbers in it change with the
    working directory (PyMCU#248):

        cd ~/Repos/PyMCU  &&  pymcu --version   ->  pymcu-compiler 0.1.0a3
        cd /tmp           &&  pymcu --version   ->  pymcu-compiler 0.1.0a9

    That is not a bug in the lookup. `_ensure_venv()` in main.py re-executes the CLI with a
    project's `.venv` interpreter when the working directory has one, deliberately, so a
    project pinning its own PyMCU gets the one it pinned. `importlib.metadata` then reports
    that installation, correctly.

    What was missing is that the table never said WHICH installation it was describing, and
    the failure was silent in the flattering direction: from inside a checkout -- where
    anyone investigating a version question is standing -- it reported the project's older
    set as though it were the machine's.

    So the number is not corrected here. The environment it came from is named.
    """
    prefix = Path(sys.prefix).resolve()
    try:
        local = (Path.cwd() / ".venv").resolve()
    except OSError:                      # cwd deleted underneath us
        return str(prefix), "global install"
    if local == prefix:
        return str(prefix), "this project's .venv, switched into automatically"
    return str(prefix), "global install"


def version():
    """
    Displays the version information for PyMCU and its components.
    """
    try:
        from importlib.metadata import version, PackageNotFoundError
    except ImportError:
        # Fallback for older Python versions if needed, though PyMCU targets 3.10+
        console.print("[red]Error: importlib.metadata not available.[/red]")
        return

    # The core two are always listed, installed or not, so a missing one is
    # visible rather than absent. Everything else is discovered: this used to
    # be a hardcoded pair, so `pymcu --version` on a machine with pymcu-avr
    # installed said nothing about the backend that was doing the work -- the
    # first thing you want when a build behaves differently on two machines.
    # Discovering beats extending the list, which is how it fell behind.
    packages = [
        ("pymcu-compiler", "Compiler & CLI Driver"),
        ("pymcu-stdlib", "Standard Library"),
    ]
    packages += _discover_companions({name for name, _ in packages})

    table = Table(title="PyMCU Ecosystem Version Info", box=box.ROUNDED)
    table.add_column("Package", style="cyan", no_wrap=True)
    table.add_column("Description", style="magenta")
    table.add_column("Version", style="green")

    for pkg_name, description in packages:
        try:
            ver = version(pkg_name)
            table.add_row(pkg_name, description, ver)
        except PackageNotFoundError:
            table.add_row(pkg_name, description, "[red]Not Installed[/red]")

    # Add Python version
    table.add_row("python", "Python Interpreter", sys.version.split()[0])

    console.print(table)

    # Which installation the rows above describe. Printed always, not only when a project
    # venv was used: "global install" is the answer that makes the other one meaningful, and
    # a line that appears only sometimes is one nobody learns to look for.
    env_path, env_how = _resolved_environment()
    console.print(f"\nEnvironment: {env_path}\n             ({env_how})", style="dim")
    console.print("\n[dim]Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors[/dim]")
