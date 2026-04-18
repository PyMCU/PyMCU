# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# This program is free software: you can redistribute it and/or modify
# it under the terms of the GNU Affero General Public License as published
# by the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# This program is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
# GNU Affero General Public License for more details.
#
# You should have received a copy of the GNU Affero General Public License
# along with this program.  If not, see <https://www.gnu.org/licenses/>.
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

import typer
from rich.console import Console
from rich.table import Table
from rich import box

from ..toolchains.avrgas import AvrgasToolchain, _TOOLCHAIN_VERSION as _AVR_VERSION
from ..toolchains.gputils import GputilsToolchain

console = Console()

toolchain_app = typer.Typer(
    name="toolchain",
    help="Manage PyMCU toolchains (assemblers / compilers).",
    no_args_is_help=True,
)

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

_TOOLCHAINS = {
    "avr": {
        "cls": AvrgasToolchain,
        "version": _AVR_VERSION,
        "description": "GNU AVR binutils (avr-as, avr-ld, avr-objcopy)",
        "sample_chip": "atmega328p",
    },
    "pic": {
        "cls": GputilsToolchain,
        "version": GputilsToolchain.METADATA["version"],
        "description": "GNU PIC Utilities (gpasm/gplink)",
        "sample_chip": "pic16f84a",
    },
}


def _make_instance(family: str):
    """Return a toolchain instance for the given family name."""
    meta = _TOOLCHAINS[family]
    chip = meta["sample_chip"]
    return meta["cls"](console, chip)


# ---------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------

@toolchain_app.command("list")
def toolchain_list():
    """List all known toolchains and their installation status."""
    table = Table(title="PyMCU Toolchains", box=box.ROUNDED)
    table.add_column("Family", style="cyan", no_wrap=True)
    table.add_column("Description", style="white")
    table.add_column("Version", style="magenta")
    table.add_column("Status", style="bold")

    for family, meta in _TOOLCHAINS.items():
        tc = _make_instance(family)
        if tc.is_cached():
            status = "[green]installed[/green]"
        else:
            status = "[dim]not installed[/dim]"
        table.add_row(family, meta["description"], meta["version"], status)

    console.print(table)


@toolchain_app.command("install")
def toolchain_install(
    family: str = typer.Argument(
        ...,
        help="Toolchain family to install: avr or pic.",
    ),
):
    """
    Install a toolchain into the local cache (~/.pymcu/tools/).

    Examples
    --------
        pymcu toolchain install avr
        pymcu toolchain install pic
    """
    if family not in _TOOLCHAINS:
        console.print(
            f"[red]Unknown toolchain family: {family!r}. "
            f"Valid options: {', '.join(_TOOLCHAINS)}[/red]"
        )
        raise typer.Exit(code=1)

    tc = _make_instance(family)

    if tc.is_cached():
        meta = _TOOLCHAINS[family]
        console.print(
            f"[green]Toolchain '{family}' (v{meta['version']}) is already installed.[/green]"
        )
        return

    try:
        tc.install()
        console.print(f"[bold green]Toolchain '{family}' installed successfully.[/bold green]")
    except RuntimeError as e:
        console.print(f"[bold red]Installation failed:[/bold red] {e}")
        raise typer.Exit(code=1)


@toolchain_app.command("update")
def toolchain_update(
    family: str = typer.Argument(
        ...,
        help="Toolchain family to update: avr or pic.",
    ),
):
    """
    Re-download and reinstall a toolchain to pick up a newer version.

    Examples
    --------
        pymcu toolchain update avr
    """
    if family not in _TOOLCHAINS:
        console.print(
            f"[red]Unknown toolchain family: {family!r}. "
            f"Valid options: {', '.join(_TOOLCHAINS)}[/red]"
        )
        raise typer.Exit(code=1)

    tc = _make_instance(family)

    # Force reinstall by wiping the version file so is_cached() returns False.
    version_file = tc._version_file()
    if version_file.exists():
        version_file.unlink()

    try:
        tc.install()
        meta = _TOOLCHAINS[family]
        console.print(
            f"[bold green]Toolchain '{family}' updated to v{meta['version']}.[/bold green]"
        )
    except RuntimeError as e:
        console.print(f"[bold red]Update failed:[/bold red] {e}")
        raise typer.Exit(code=1)
