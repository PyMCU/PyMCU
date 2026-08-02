# -----------------------------------------------------------------------------
# PyMCU CLI Driver -- board/chip catalog (`pymcu boards`)
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
# Machine-readable catalog of the boards and chips this installation supports.
# IDE integrations (VS Code / JetBrains) consume `pymcu boards --json` instead
# of hardcoding board lists that drift from BOARD_CHIPS/BOARD_GROUPS.
# -----------------------------------------------------------------------------

import json as _json

import typer
from rich.console import Console
from rich.table import Table

from ..core.boards import BOARD_CHIPS, BOARD_GROUPS, default_programmer, default_toolchain

console = Console()


def _catalog() -> dict:
    from .new import get_available_chips

    group_of = {b: g for g, names in BOARD_GROUPS.items() for b in names}
    boards = [
        {
            "name": name,
            "chip": chip,
            "group": group_of.get(name),
            "toolchain": default_toolchain(chip),
            "programmer": default_programmer(chip),
        }
        for name, chip in BOARD_CHIPS.items()
    ]
    return {
        "boards": boards,
        "groups": BOARD_GROUPS,
        "chips": get_available_chips(),
    }


def boards(
    json_output: bool = typer.Option(
        False, "--json", help="Emit the catalog as JSON on stdout (for IDE integrations)."),
) -> None:
    """List the boards and chips supported by this PyMCU installation."""
    catalog = _catalog()

    if json_output:
        print(_json.dumps(catalog))
        return

    table = Table(title="Supported boards")
    table.add_column("Board", style="bold")
    table.add_column("Chip")
    table.add_column("Group", style="dim")
    table.add_column("Toolchain", style="dim")
    table.add_column("Programmer", style="dim")
    for b in catalog["boards"]:
        table.add_row(b["name"], b["chip"], b["group"] or "-", b["toolchain"], b["programmer"])
    console.print(table)

    if catalog["chips"]:
        console.print(
            f"\nInstalled chip definitions ({len(catalog['chips'])}): "
            + ", ".join(catalog["chips"]))
    else:
        console.print("\n[yellow]pymcu-stdlib not installed -- no chip definitions found.[/]")
