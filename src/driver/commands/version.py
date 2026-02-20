# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# This file is part of the PyMCU Development Ecosystem.
#
# PyMCU is free software: you can redistribute it and/or modify
# it under the terms of the GNU General Public License as published by
# the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# PyMCU is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
# GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with PyMCU.  If not, see <https://www.gnu.org/licenses/>.
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

    # Define packages to check
    packages = [
        ("pymcu-compiler", "Compiler Core"),
        ("pymcu-stdlib", "Standard Library"),
        ("pymcu-driver", "CLI Driver"),
    ]

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
