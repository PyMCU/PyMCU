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

from ..toolchains import discover_plugins

console = Console()

toolchain_app = typer.Typer(
    name="toolchain",
    help="Manage PyMCU toolchains (assemblers / compilers).",
    no_args_is_help=True,
)

# ---------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------

@toolchain_app.command("list")
def toolchain_list():
    """List all installed toolchain plugins and their installation status."""
    plugins = discover_plugins()

    if not plugins:
        console.print(
            "[yellow]No toolchain plugins installed.[/yellow]\n"
            "Install one with:  [bold]pip install pymcu[avr][/bold]  "
            "or  [bold]pip install pymcu[pic][/bold]"
        )
        return

    table = Table(title="PyMCU Toolchains", box=box.ROUNDED)
    table.add_column("Family", style="cyan", no_wrap=True)
    table.add_column("Description", style="white")
    table.add_column("Version", style="magenta")
    table.add_column("Status", style="bold")

    for family, plugin_cls in plugins.items():
        tc = plugin_cls.get_instance(console)
        status = "[green]installed[/green]" if tc.is_cached() else "[dim]not installed[/dim]"
        table.add_row(family, plugin_cls.description, plugin_cls.version, status)

    console.print(table)


@toolchain_app.command("install")
def toolchain_install(
    family: str = typer.Argument(
        ...,
        help="Toolchain family to install (e.g. avr, pic).",
    ),
):
    """
    Install a toolchain into the local cache (~/.pymcu/tools/).

    Examples
    --------
        pymcu toolchain install avr
        pymcu toolchain install pic
    """
    plugins = discover_plugins()

    if family not in plugins:
        if plugins:
            console.print(
                f"[red]Unknown toolchain family: {family!r}. "
                f"Installed plugins: {', '.join(plugins)}[/red]"
            )
        else:
            console.print(
                f"[red]No toolchain plugins installed.[/red]\n"
                f"Install one first, e.g.:  [bold]pip install pymcu[{family}][/bold]"
            )
        raise typer.Exit(code=1)

    plugin_cls = plugins[family]
    tc = plugin_cls.get_instance(console)

    if tc.is_cached():
        console.print(
            f"[green]Toolchain '{family}' (v{plugin_cls.version}) is already installed.[/green]"
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
        help="Toolchain family to update (e.g. avr, pic).",
    ),
):
    """
    Re-download and reinstall a toolchain to pick up a newer version.

    Examples
    --------
        pymcu toolchain update avr
    """
    plugins = discover_plugins()

    if family not in plugins:
        if plugins:
            console.print(
                f"[red]Unknown toolchain family: {family!r}. "
                f"Installed plugins: {', '.join(plugins)}[/red]"
            )
        else:
            console.print(
                f"[red]No toolchain plugins installed.[/red]\n"
                f"Install one first, e.g.:  [bold]pip install pymcu[{family}][/bold]"
            )
        raise typer.Exit(code=1)

    plugin_cls = plugins[family]
    tc = plugin_cls.get_instance(console)

    # Force reinstall by wiping the version file so is_cached() returns False.
    version_file = tc._version_file()
    if version_file.exists():
        version_file.unlink()

    try:
        tc.install()
        console.print(
            f"[bold green]Toolchain '{family}' updated to v{plugin_cls.version}.[/bold green]"
        )
    except RuntimeError as e:
        console.print(f"[bold red]Update failed:[/bold red] {e}")
        raise typer.Exit(code=1)
