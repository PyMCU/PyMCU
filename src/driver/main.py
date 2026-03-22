# -----------------------------------------------------------------------------
# Whipsnake CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
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
        os.environ["WHIP_VERBOSE"] = "1"

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
                    local_exe = venv_path / "Scripts" / "whip.exe"
                else:
                    local_exe = venv_path / "bin" / "whip"
                
                if is_verbose:
                    console.print(f"[debug] Checking local executable: {local_exe}", style="dim")
                
                # If the local whip executable exists, switch to it
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
from .commands.version import version

app = typer.Typer(help="whip: Python-to-MCU compiler driver")

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
        os.environ["WHIP_VERBOSE"] = "1"

app.command()(new)
app.command()(build)
app.command()(clean)
app.command()(flash)

def run_cli():
    _ensure_venv()
    app()

if __name__ == "__main__":
    run_cli()
