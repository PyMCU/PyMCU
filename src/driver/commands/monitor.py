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
import sys
from pathlib import Path
from typing import Optional

import tomlkit
import typer
from rich.console import Console

from ..core import serial_port

console = Console()
err_console = Console(stderr=True)

DEFAULT_BAUD = 115_200


def _resolve_port(explicit: str | None, configured: str | None,
                  candidates: list[str]) -> str | None:
    """The port to monitor, in the order `pymcu flash` resolves its own:
    the command line, then [tool.pymcu.flash].port, then a lone auto-detected
    device. Returns None when the answer has to come from the user.
    """
    if explicit:
        return explicit
    if configured:
        return configured
    if len(candidates) == 1:
        return candidates[0]
    return None


def _resolve_baud(explicit: int | None, pymcu_config) -> int:
    """--baud, then [tool.pymcu] stdout_baud -- the speed `print()` runs at --
    then the driver's default. `is not None`, not truthiness: --baud 0 must
    reach the caller and be rejected there, not silently read as "unset"."""
    if explicit is not None:
        return explicit
    configured = pymcu_config.get("stdout_baud")
    if configured:
        return int(configured)
    return DEFAULT_BAUD


def monitor(
    verbose: bool = typer.Option(False, "--verbose", "-v", help="Enable verbose logging"),
    port: Optional[str] = typer.Option(
        None, "--port", "-P",
        help="Serial port to read (e.g. COM3 on Windows, /dev/cu.usbmodemXXXX on "
             "macOS, /dev/ttyACM0 on Linux). "
             "Overrides \\[tool.pymcu.flash].port in pyproject.toml.",
    ),
    baud: Optional[int] = typer.Option(
        None, "--baud", "-b",
        help="Baud rate. Overrides \\[tool.pymcu].stdout_baud "
             f"(default {DEFAULT_BAUD}).",
    ),
    no_send: bool = typer.Option(
        False, "--no-send",
        help="Read only: do not forward stdin to the board.",
    ),
):
    """
    Streams what the firmware prints over UART, and forwards what you type
    back to the board.

    Port resolution order:
      1. --port / -P CLI argument
      2. port = "..." in \\[tool.pymcu.flash] of pyproject.toml
      3. Auto-detection (a single matching USB-serial device)

    Baud resolution order:
      1. --baud / -b CLI argument
      2. stdout_baud = "..." in \\[tool.pymcu] of pyproject.toml
      3. 115200

    Works outside a project too: with --port there is nothing to read from
    pyproject.toml and none is required.
    """
    pymcu_config = {}
    pyproject_path = Path("pyproject.toml")
    if pyproject_path.exists():
        with open(pyproject_path, "r") as f:
            pymcu_config = tomlkit.load(f).get("tool", {}).get("pymcu", {})

    candidates = serial_port.candidate_ports()
    resolved_port = _resolve_port(
        port, pymcu_config.get("flash", {}).get("port"), candidates)
    if resolved_port is None:
        if not candidates:
            err_console.print(
                "[red]No serial port found.[/red] Connect the board, or pass "
                "one with [bold]--port[/bold]."
            )
        else:
            err_console.print(
                "[red]More than one serial port is connected -- say which:[/red]"
            )
            for candidate in candidates:
                err_console.print(f"  [bold]--port[/bold] {candidate}")
        raise typer.Exit(code=1)

    resolved_baud = _resolve_baud(baud, pymcu_config)
    if resolved_baud <= 0:
        err_console.print("[red]Baud rate must be a positive number.[/red]")
        raise typer.Exit(code=1)

    send = not no_send
    # The header goes to stderr: stdout carries firmware bytes and nothing
    # else, so `pymcu monitor | tee log` captures exactly what the board said.
    err_console.print(
        f"[dim]pymcu monitor: {resolved_port} at {resolved_baud} baud"
        f"{'' if send else ' (read-only)'} -- Ctrl+C to exit[/dim]"
    )

    if sys.platform == "win32":
        code = serial_port.monitor_windows(
            resolved_port, resolved_baud, sys.stdout.buffer,
            sys.stdin.buffer if send else None, send,
        )
        if code == -1:
            err_console.print(
                "[red]The serial monitor on Windows needs pyserial:[/red] "
                "[bold]pip install pyserial[/bold]"
            )
            raise typer.Exit(code=1)
        raise typer.Exit(code=code)

    try:
        fd = serial_port.open_port(resolved_port, resolved_baud)
    except ValueError as e:
        err_console.print(f"[red]{e}[/red]")
        raise typer.Exit(code=1)
    except OSError as e:
        err_console.print(
            f"[red]Could not open {resolved_port}:[/red] {e.strerror or e}"
        )
        raise typer.Exit(code=1)

    try:
        # fileno() raises when stdin is not a real descriptor -- a test
        # runner's captured stdin, an embedded interpreter, a closed fd --
        # and the monitor still owes the user the read half.
        input_fd = None
        if send:
            try:
                input_fd = sys.stdin.fileno()
            except (OSError, ValueError, AttributeError):
                pass
        serial_port.pump(fd, input_fd, sys.stdout.buffer)
        exit_code = 0
    except serial_port.DeviceGone:
        err_console.print(
            f"[yellow]{resolved_port} stopped answering[/yellow] -- the board "
            "was unplugged or reset its USB connection."
        )
        exit_code = 2
    except KeyboardInterrupt:
        exit_code = 0
    finally:
        try:
            os.close(fd)
        except OSError:
            pass

    raise typer.Exit(code=exit_code)
