# -----------------------------------------------------------------------------
# PyMCU CLI Driver -- `pymcu home`
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

import threading
import webbrowser

import typer
from rich.console import Console

from ..commands.libraries import _load_project
from ..home.server import serve

console = Console()


def home(
    port: int = typer.Option(
        0, "--port", help="Port to listen on. 0 picks a free one."),
    open_browser: bool = typer.Option(
        True, "--open/--no-open", help="Open the page in a browser."),
):
    """Browse and install PyMCU libraries for this project from a browser."""
    project = _load_project()
    httpd, token = serve(project, port=port)
    url = f"http://127.0.0.1:{httpd.server_port}/?token={token}"

    console.print(f"[bold]PyMCU libraries[/bold] for {project.board or project.chip or 'this project'}")
    console.print(f"  {url}")
    console.print("[dim]Loopback only, and the token dies with this process. Ctrl-C to stop.[/dim]")

    if open_browser:
        # After the server is listening, so the first request cannot beat it.
        threading.Timer(0.2, webbrowser.open, args=(url,)).start()

    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        console.print("\n[dim]Stopped.[/dim]")
    finally:
        httpd.server_close()
