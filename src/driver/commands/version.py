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
    console.print("\n[dim]Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors[/dim]")
