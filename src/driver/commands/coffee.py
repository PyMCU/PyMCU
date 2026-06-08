# -----------------------------------------------------------------------------
# PyMCU CLI Driver — the most important command
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------

import random
import time
import typer
from rich.console import Console
from rich.text import Text

console = Console()

_COFFEE = r"""
        ( (
         ) )
      ._______.
      |       |]
      \       /
       `-----'
"""

_QUOTES = [
    "Embedded engineers don't sleep. They poll.",
    "There are 10 types of developers: those who debug at 3am and those who haven't yet.",
    "It works on my Arduino.",
    "Have you tried turning it off and on again? (It's called a watchdog timer.)",
    "The LED blinked. Ship it.",
    "Undefined behavior: the feature, not the bug.",
    "Real-time means it crashed in real time.",
    "0xFF problems and a bit ain't one.",
    "Stack overflow? That's not a website, that's a Monday.",
    "My code doesn't have bugs. It has unspecified features at unknown memory addresses.",
    "I don't always test my firmware, but when I do, I do it in production.",
    "In embedded, every byte counts. Especially the ones you forgot to initialize.",
]


def coffee():
    """..."""  # hidden from help on purpose
    console.print()
    console.print(Text(_COFFEE, style="bold yellow"), justify="center")
    console.print(
        Text("  PyMCU Fuel Station  ", style="bold white on dark_red"),
        justify="center",
    )
    console.print()

    quote = random.choice(_QUOTES)
    console.print(f'[dim italic]  "{quote}"[/dim italic]')
    console.print()

    with console.status("[bold yellow]Brewing...[/bold yellow]", spinner="dots"):
        time.sleep(1.5)

    console.print("[bold green]  Coffee ready. Now go flash something.[/bold green]")
    console.print()
