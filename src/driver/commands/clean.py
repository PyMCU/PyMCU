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

import os
import stat
import shutil
import sys
from pathlib import Path
import typer
from rich.console import Console

console = Console()


def _on_rmtree_error(func, path, exc_info):
    """rmtree error handler that clears the read-only bit and retries.

    On Windows, files extracted from archives (or any read-only artifact) make
    shutil.rmtree raise PermissionError. Clearing the read-only attribute and
    retrying the failed operation lets the removal complete. On POSIX this is a
    harmless no-op for the rare read-only file. Compatible with both the legacy
    `onerror` (3.11) and `onexc` (3.12+) rmtree callback signatures.
    """
    try:
        os.chmod(path, stat.S_IWRITE)
        func(path)
    except OSError:
        pass


def clean():
    """
    Removes build artifacts (dist/ directory, including dist/_generated/).
    """
    dist_dir = Path("dist")

    if dist_dir.exists():
        try:
            # Use an error handler (not ignore_errors=True): on Windows read-only
            # files would otherwise be silently left behind while we still report
            # success. Python 3.12 renamed the keyword from onerror to onexc.
            if sys.version_info >= (3, 12):
                shutil.rmtree(dist_dir, onexc=_on_rmtree_error)
            else:
                shutil.rmtree(dist_dir, onerror=_on_rmtree_error)
            console.print(f"[bold green]+[/bold green] Cleaned build artifacts in '{dist_dir}'.")
        except Exception as e:
            console.print(f"[bold red]Error cleaning '{dist_dir}':[/bold red] {e}")
            raise typer.Exit(code=1)
    else:
        console.print("[yellow]Nothing to clean (dist/ directory does not exist).[/yellow]")
