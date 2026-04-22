# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

import shutil
from pathlib import Path
import typer
from rich.console import Console

console = Console()

def clean():
    """
    Removes build artifacts (dist/ directory, including dist/_generated/).
    """
    dist_dir = Path("dist")
    
    if dist_dir.exists():
        try:
            shutil.rmtree(dist_dir, ignore_errors=True)
            console.print(f"[bold green]+[/bold green] Cleaned build artifacts in '{dist_dir}'.")
        except Exception as e:
            console.print(f"[bold red]Error cleaning '{dist_dir}':[/bold red] {e}")
            raise typer.Exit(code=1)
    else:
        console.print("[yellow]Nothing to clean (dist/ directory does not exist).[/yellow]")
