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

import sys
import os
from pathlib import Path
import typer
from rich.console import Console
from typing import Optional

console = Console()

def _ensure_venv():
    """
    Automatic Venv Switching (The "Wrapper" Logic)
    Checks if we are running globally but a local .venv exists.
    If so, re-executes the current command using the local venv's interpreter.
    """
    # Always check for local .venv, even if we are in a venv (e.g. pipx)
    cwd = Path.cwd()
    venv_path = cwd / ".venv"
    
    is_verbose = "--verbose" in sys.argv or "-v" in sys.argv
    if is_verbose:
        os.environ["PYMCU_VERBOSE"] = "1"

    if is_verbose:
        console.print(f"[debug] _ensure_venv() called", style="dim")
        console.print(f"[debug] Current working directory: {cwd}", style="dim")
        console.print(f"[debug] sys.executable: {sys.executable}", style="dim")
        console.print(f"[debug] sys.prefix: {sys.prefix}", style="dim")
        console.print(f"[debug] Looking for venv at: {venv_path}", style="dim")
        console.print(f"[debug] venv exists: {venv_path.exists()}", style="dim")

    if venv_path.exists() and venv_path.is_dir():
        # Check if we are already using this venv
        try:
            current_prefix = Path(sys.prefix).resolve()
            target_prefix = venv_path.resolve()

            if is_verbose:
                console.print(f"[debug] Current prefix: {current_prefix}", style="dim")
                console.print(f"[debug] Target prefix: {target_prefix}", style="dim")

            if current_prefix != target_prefix:
                 # Determine executable path based on platform
                if sys.platform == "win32":
                    local_exe = venv_path / "Scripts/pymcu.exe"
                else:
                    local_exe = venv_path / "bin" / "pymcu"
                
                if is_verbose:
                    console.print(f"[debug] Checking local executable: {local_exe}", style="dim")
                
                # If the local pymcu executable exists, switch to it
                if local_exe.exists():
                    if is_verbose:
                        console.print(f"[debug] Switching to local venv: {local_exe}", style="dim")
                    # Replace current process with the local venv version.
                    # Guard against symlink loops (e.g. project dir is itself a symlink).
                    try:
                        os.execv(str(local_exe), [str(local_exe)] + sys.argv[1:])
                    except (OSError, PermissionError) as exec_err:
                        if is_verbose:
                            console.print(
                                f"[debug] execv failed ({exec_err}), continuing with current interpreter",
                                style="dim",
                            )
                else:
                     if is_verbose:
                        console.print(f"[debug] Local executable not found at {local_exe}", style="dim")
            else:
                if is_verbose:
                    console.print(f"[debug] Already using target venv, no switch needed", style="dim")
        except Exception as e:
            if is_verbose:
                console.print(f"[debug] Venv switch failed: {e}", style="dim")
            pass # Fallback if resolution fails or execv fails
    else:
        if is_verbose:
            console.print(f"[debug] No local .venv found, continuing with current Python", style="dim")


# Application definition
from .commands.new import new
from .commands.build import build
from .commands.clean import clean
from .commands.flash import flash
from .commands.version import version
from .commands.toolchain import toolchain_app

app = typer.Typer(help="pymcu: Python-to-MCU compiler driver")

def version_callback(value: bool):
    if value:
        version()
        raise typer.Exit()

@app.callback()
def main(
    verbose: bool = typer.Option(False, "--verbose", "-v", help="Enable verbose logging globally"),
    version_flag: Optional[bool] = typer.Option(None, "--version", callback=version_callback, is_eager=True, help="Show the version and exit")
):
    if verbose:
        os.environ["PYMCU_VERBOSE"] = "1"

app.command()(new)
app.command()(build)
app.command()(clean)
app.command()(flash)
app.add_typer(toolchain_app)

def run_cli():
    _ensure_venv()
    app()

if __name__ == "__main__":
    run_cli()
