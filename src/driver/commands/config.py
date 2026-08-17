# -----------------------------------------------------------------------------
# PyMCU CLI Driver -- `pymcu config`
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

import json
from typing import Optional

import typer
from rich.console import Console
from rich.table import Table
from rich import box

from ..commands.libraries import _load_project
from ..core.project_config import apply_changes, describe

console = Console()


def config(
    board: Optional[str] = typer.Option(None, "--board", help="Target board."),
    layer: Optional[str] = typer.Option(
        None, "--layer", help="API layer: native, micropython or circuitpython."),
    frequency: Optional[int] = typer.Option(None, "--frequency", help="Clock in hertz."),
    sources: Optional[str] = typer.Option(None, "--sources", help="Source directory."),
    entry: Optional[str] = typer.Option(None, "--entry", help="Entry point file."),
    json_output: bool = typer.Option(
        False, "--json", help="Emit the settings as JSON (for IDE integrations)."),
):
    """Show this project's build settings, or change them in pyproject.toml."""
    project = _load_project()
    wants_change = any(v is not None for v in (board, layer, frequency, sources, entry))

    if wants_change:
        result = apply_changes(
            project.path, project.doc,
            board=board, layer=layer, frequency=frequency, sources=sources, entry=entry,
        )
        if not result.ok:
            console.print(f"[red]{result.message}[/red]")
            raise typer.Exit(code=1)
        console.print(f"[bold green]✓[/bold green] {result.message}")
        for key, value in result.changed.items():
            console.print(f"  {key} = {value}")
        # Re-read so what is printed below is what the file now says.
        project = _load_project()

    settings = describe(project.doc, project.root)

    if json_output:
        print(json.dumps(settings))
        return

    table = Table(box=box.SIMPLE, show_header=False, pad_edge=False)
    table.add_column(style="dim")
    table.add_column()

    freq = settings["frequency"]
    freq_label = f"{freq / 1_000_000:g} MHz" if freq else "-"
    if freq and not settings["frequency_explicit"]:
        freq_label += "  (board default)"

    table.add_row("board", settings["board"] or f"(target = {settings['target'] or '-'})")
    table.add_row("chip", settings["chip"] or "-")
    table.add_row("layer", settings["layer"])
    table.add_row("clock", freq_label)
    table.add_row("sources", settings["sources"] + ("" if settings["sources_exist"] else "  (missing)"))
    table.add_row("entry", settings["entry"] + ("" if settings["entry_exists"] else "  (missing)"))
    table.add_row("toolchain", settings["toolchain"] or "-")
    table.add_row("programmer", settings["programmer"] or "-")

    console.print(table)
    if not wants_change:
        console.print("[dim]Change one with: pymcu config --board arduino_uno --layer micropython[/dim]")
