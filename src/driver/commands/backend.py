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

"""
pymcu backend -- manage PyMCU codegen backend plugins.

Sub-commands:
  list     -- show installed backends and their license status
  install  -- install a backend package via pip
  check    -- validate licenses for all installed backends
"""

import json
import subprocess
import sys
import typer
from rich.console import Console
from rich.table import Table
from rich import box

from ..backends import discover_backends

console = Console()

backend_app = typer.Typer(
    name="backend",
    help="Manage PyMCU codegen backend plugins.",
    no_args_is_help=True,
)

# ---------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------

@backend_app.command("list")
def backend_list(
    as_json: bool = typer.Option(False, "--json", help="Output as JSON (for IDE integrations).")
):
    """List all installed backend plugins and their license status."""
    plugins = discover_backends()

    if not plugins:
        if as_json:
            print(json.dumps([]))
        else:
            console.print("[yellow]No backend plugins installed.[/yellow]")
            console.print("Install one with:  [bold]pymcu backend install avr[/bold]")
        return

    if as_json:
        result = []
        for family, cls in plugins.items():
            try:
                status = cls.validate_license()
                status_str = status.name.lower()
            except Exception:
                status_str = "unknown"
            result.append({
                "family": family,
                "description": getattr(cls, "description", ""),
                "version": getattr(cls, "version", ""),
                "license_status": status_str,
                "binary": str(cls.get_backend_binary()),
            })
        print(json.dumps(result, indent=2))
        return

    table = Table(box=box.SIMPLE_HEAD)
    table.add_column("Family",      style="cyan",  no_wrap=True)
    table.add_column("Description", style="white")
    table.add_column("Version",     style="dim")
    table.add_column("License",     style="green")
    table.add_column("Binary",      style="dim")

    for family, cls in plugins.items():
        try:
            status = cls.validate_license()
            from pymcu_backend_sdk import LicenseStatus
            if status == LicenseStatus.VALID:
                license_label = "[green]valid[/green]"
            elif status == LicenseStatus.MISSING:
                license_label = "[yellow]missing[/yellow]"
            elif status == LicenseStatus.EXPIRED:
                license_label = "[red]expired[/red]"
            else:
                license_label = f"[red]{status.name.lower()}[/red]"
        except Exception:
            license_label = "[dim]unknown[/dim]"

        binary = cls.get_backend_binary()
        binary_exists = binary.exists() if binary else False
        binary_label = str(binary) if binary_exists else f"[red]{binary} (missing)[/red]"

        table.add_row(
            family,
            getattr(cls, "description", ""),
            getattr(cls, "version", ""),
            license_label,
            binary_label,
        )

    console.print(table)


@backend_app.command("install")
def backend_install(
    family: str = typer.Argument(..., help="Backend family to install, e.g. 'avr', 'pic', 'riscv'.")
):
    """Install a backend plugin package (wraps pip install)."""
    package = f"pymcu-backend-{family}"
    console.print(f"[cyan]Installing[/cyan] {package} ...")
    try:
        subprocess.run(
            [sys.executable, "-m", "pip", "install", package],
            check=True,
        )
        console.print(f"[green]Installed[/green] {package}.")
    except subprocess.CalledProcessError:
        console.print(f"[bold red]Failed to install {package}.[/bold red]")
        console.print(f"Run manually:  pip install {package}")
        raise typer.Exit(code=1)


@backend_app.command("check")
def backend_check():
    """Validate licenses for all installed backend plugins."""
    plugins = discover_backends()

    if not plugins:
        console.print("[yellow]No backend plugins installed.[/yellow]")
        return

    all_ok = True
    for family, cls in plugins.items():
        try:
            from pymcu_backend_sdk import LicenseStatus
            status = cls.validate_license()
            if status == LicenseStatus.VALID:
                console.print(f"[green]{family}[/green]: license valid")
            elif status == LicenseStatus.MISSING:
                console.print(
                    f"[yellow]{family}[/yellow]: license missing  "
                    f"(set PYMCU_LICENSE_KEY or place key at ~/.pymcu/license.key)"
                )
                all_ok = False
            elif status == LicenseStatus.EXPIRED:
                console.print(f"[red]{family}[/red]: license expired  (renew at https://pymcu.dev/renew)")
                all_ok = False
            else:
                console.print(f"[red]{family}[/red]: license invalid ({status.name})")
                all_ok = False
        except Exception as exc:
            console.print(f"[red]{family}[/red]: check failed ({exc})")
            all_ok = False

    if not all_ok:
        raise typer.Exit(code=1)
