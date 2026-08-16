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
        console.print(f"\\[debug] _ensure_venv() called", style="dim")
        console.print(f"\\[debug] Current working directory: {cwd}", style="dim")
        console.print(f"\\[debug] sys.executable: {sys.executable}", style="dim")
        console.print(f"\\[debug] sys.prefix: {sys.prefix}", style="dim")
        console.print(f"\\[debug] Looking for venv at: {venv_path}", style="dim")
        console.print(f"\\[debug] venv exists: {venv_path.exists()}", style="dim")

    if venv_path.exists() and venv_path.is_dir():
        # Check if we are already using this venv
        try:
            current_prefix = Path(sys.prefix).resolve()
            target_prefix = venv_path.resolve()

            if is_verbose:
                console.print(f"\\[debug] Current prefix: {current_prefix}", style="dim")
                console.print(f"\\[debug] Target prefix: {target_prefix}", style="dim")

            if current_prefix != target_prefix:
                 # Determine executable path based on platform
                if sys.platform == "win32":
                    local_exe = venv_path / "Scripts/pymcu.exe"
                else:
                    local_exe = venv_path / "bin" / "pymcu"
                
                if is_verbose:
                    console.print(f"\\[debug] Checking local executable: {local_exe}", style="dim")
                
                # If the local pymcu executable exists, switch to it
                if local_exe.exists():
                    if is_verbose:
                        console.print(f"\\[debug] Switching to local venv: {local_exe}", style="dim")
                    # Hand off to the local venv version.
                    # Guard against symlink loops (e.g. project dir is itself a symlink).
                    try:
                        if sys.platform == "win32":
                            # Windows has no real exec() that replaces the running
                            # image: os.execv spawns a detached child and lets the
                            # parent return asynchronously, so the shell regains the
                            # prompt while the child is still writing -> interleaved
                            # output and unreliable exit codes. Run the child
                            # synchronously and propagate its exit code instead.
                            import subprocess
                            completed = subprocess.run([str(local_exe)] + sys.argv[1:])
                            sys.exit(completed.returncode)
                        else:
                            # POSIX: replace the current process image in place.
                            os.execv(str(local_exe), [str(local_exe)] + sys.argv[1:])
                    except (OSError, PermissionError) as exec_err:
                        if is_verbose:
                            console.print(
                                f"\\[debug] exec/relaunch failed ({exec_err}), continuing with current interpreter",
                                style="dim",
                            )
                else:
                    # Hay entorno del proyecto pero PyMCU no esta dentro, asi que
                    # se sigue con el global. Silenciarlo es lo peor de los dos
                    # mundos: el proyecto declara sus versiones en pyproject.toml
                    # y no se usa ninguna, sin que nadie lo diga. Lo tipico es que
                    # las dependencias no se llegaran a instalar.
                    console.print(
                        "[yellow]Note:[/yellow] this project has a .venv but PyMCU is not "
                        "installed in it, so the global installation is being used and the "
                        "versions pinned in pyproject.toml are ignored.",
                        style="dim",
                    )
                    console.print(
                        "[dim]      Install them with `uv sync`, `poetry install` or "
                        "`.venv/bin/pip install -r requirements.txt`.[/dim]"
                    )
                    if is_verbose:
                        console.print(f"\\[debug] Local executable not found at {local_exe}", style="dim")
            else:
                if is_verbose:
                    console.print(f"\\[debug] Already using target venv, no switch needed", style="dim")
        except Exception as e:
            if is_verbose:
                console.print(f"\\[debug] Venv switch failed: {e}", style="dim")
            pass # Fallback if resolution fails or execv fails
    else:
        if is_verbose:
            console.print(f"\\[debug] No local .venv found, continuing with current Python", style="dim")


# Application definition
from .commands.new import new
from .commands.build import build
from .commands.clean import clean
from .commands.flash import flash
from .commands.sync import sync
from .commands.version import version
from .commands.toolchain import toolchain_app
from .commands.backend import backend_app
from .commands.profile import profile
from .commands.lint import lint
from .commands.boards import boards
from .commands.stubs import stubs
from .commands.bench import bench
from .commands.upgrade import upgrade
from .commands.coffee import coffee
from .commands.libraries import install, libraries, search, uninstall
from .commands.library_index import index_app

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
# `version` backs the --version flag, but people type `pymcu version` too and
# used to get "No such command". bench/profile/coffee stay hidden on purpose:
# they work, they are just not part of the advertised surface.
app.command()(version)
app.command()(clean)
app.command()(flash)
app.command()(sync)
app.command()(upgrade)
app.command(hidden=True)(coffee)
app.command(hidden=True)(profile)
app.command()(lint)
app.command()(install)
app.command()(uninstall)
app.command(name="libraries")(libraries)
app.command()(search)
app.command()(boards)
app.command()(stubs)
app.command(hidden=True)(bench)
app.add_typer(toolchain_app)
app.add_typer(backend_app)
app.add_typer(index_app)

def _force_utf8_console():
    """Make stdout/stderr UTF-8 on Windows.

    The driver prints box-drawing characters, em dashes and arrows via rich.
    When output is a real terminal rich handles encoding, but when it is
    redirected or piped (CI logs, VS Code task output, `pymcu build > log`)
    Python falls back to the locale code page (cp1252), turning non-ASCII into
    mojibake (e.g. '—' -> '?'). Reconfiguring to UTF-8 fixes every such string
    at once instead of de-Unicode-ing them one by one. No-op on POSIX, where the
    default is already UTF-8.
    """
    if sys.platform != "win32":
        return
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8")
        except (AttributeError, ValueError, OSError):
            pass


def run_cli():
    _force_utf8_console()
    _ensure_venv()
    app()

if __name__ == "__main__":
    run_cli()
