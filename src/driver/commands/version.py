# -----------------------------------------------------------------------------
# Whisnake CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the Whisnake Project Authors
#
# SPDX-License-Identifier: MIT
#
# Permission is hereby granted, free of charge, to any person obtaining a copy
# of this software and associated documentation files (the "Software"), to deal
# in the Software without restriction, including without limitation the rights
# to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
# copies of the Software, and to permit persons to whom the Software is
# furnished to do so, subject to the following conditions:
#
# The above copyright notice and this permission notice shall be included in
# all copies or substantial portions of the Software.
#
# THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
# IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
# FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
# AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
# LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
# OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
# THE SOFTWARE.
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
    Displays the version information for Whisnake and its components.
    """
    try:
        from importlib.metadata import version, PackageNotFoundError
    except ImportError:
        # Fallback for older Python versions if needed, though Whisnake targets 3.10+
        console.print("[red]Error: importlib.metadata not available.[/red]")
        return

    # Define packages to check
    packages = [
        ("whip-compiler", "Compiler & CLI Driver"),
        ("whisnake-stdlib", "Standard Library"),
    ]

    table = Table(title="Whisnake Ecosystem Version Info", box=box.ROUNDED)
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
    console.print("\n[dim]Copyright (C) 2026 Ivan Montiel Cardona and the Whisnake Project Authors[/dim]")
