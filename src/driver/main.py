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
import os
from pathlib import Path
import typer
from rich.console import Console

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
                    local_exe = venv_path / "Scripts" / "pymcu.exe"
                else:
                    local_exe = venv_path / "bin" / "pymcu"
                
                if is_verbose:
                    console.print(f"[debug] Checking local executable: {local_exe}", style="dim")
                
                # If the local pymcu executable exists, switch to it
                if local_exe.exists():
                    if is_verbose:
                        console.print(f"[debug] Switching to local venv: {local_exe}", style="dim")
                    # Replace current process with the local venv version
                    # We use os.execv to replace the current process
                    os.execv(str(local_exe), [str(local_exe)] + sys.argv[1:])
                else:
                     if is_verbose:
                        console.print(f"[debug] Local executable not found at {local_exe}", style="dim")
        except Exception as e:
            if is_verbose:
                console.print(f"[debug] Venv switch failed: {e}", style="dim")
            pass # Fallback if resolution fails or execv fails


# Application definition
from .commands.new import new
from .commands.build import build
from .commands.clean import clean
from .commands.flash import flash

app = typer.Typer(help="pymcu: Python-to-MCU compiler driver")

@app.callback()
def main(verbose: bool = typer.Option(False, "--verbose", "-v", help="Enable verbose logging globally")):
    if verbose:
        os.environ["PYMCU_VERBOSE"] = "1"

app.command()(new)
app.command()(build)
app.command()(clean)
app.command()(flash)

def run_cli():
    #_ensure_venv()
    app()

if __name__ == "__main__":
    run_cli()
