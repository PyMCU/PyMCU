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

from pathlib import Path
from typing import Optional
import tomlkit
import typer
from rich.console import Console
from ..programmers import get_programmer
from ..core.boards import BOARD_CHIPS, default_programmer

console = Console()


def _default_programmer(chip: str) -> str:
    return default_programmer(chip)


def flash(
    verbose: bool = typer.Option(False, "--verbose", "-v", help="Enable verbose logging"),
    port: Optional[str] = typer.Option(
        None, "--port", "-P",
        help="Serial port for flashing (e.g. /dev/cu.usbmodem14101). "
             "Overrides [tool.pymcu.flash].port in pyproject.toml.",
    ),
):
    """
    Flashes the built firmware to the target microcontroller.

    Port resolution order:
      1. --port / -P CLI argument
      2. port = "..." in [tool.pymcu.flash] of pyproject.toml
      3. Auto-detection (first matching USB-serial device)
      4. Error with configuration instructions
    """
    pyproject_path = Path("pyproject.toml")
    if not pyproject_path.exists():
        console.print("[red]No pyproject.toml found. Are you in a pymcu project?[/red]")
        raise typer.Exit(code=1)

    try:
        # 1. Read project config
        with open(pyproject_path, "r") as f:
            config = tomlkit.load(f)

        pymcu_config = config.get("tool", {}).get("pymcu", {})

        chip = pymcu_config.get("target") or pymcu_config.get("chip") or BOARD_CHIPS.get(
            str(pymcu_config.get("board", "")).replace("-", "_"), ""
        )
        if not chip:
            console.print(
                "[red]No 'target' or 'board' specified in [tool.pymcu] of pyproject.toml.[/red]"
            )
            raise typer.Exit(code=1)

        flash_config = pymcu_config.get("flash", {})
        programmer_name = flash_config.get("programmer") or _default_programmer(chip)
        cfg_port = flash_config.get("port")
        cfg_baud = flash_config.get("baud")

        # CLI --port takes priority over pyproject.toml
        resolved_port: str | None = port or cfg_port or None
        resolved_baud: int | None = int(cfg_baud) if cfg_baud else None

        # 2. Check for firmware artifact
        hex_file = Path("dist") / "firmware.hex"
        if not hex_file.exists():
            console.print("[red]Firmware file 'dist/firmware.hex' not found.[/red]")
            console.print("Please run [bold]pymcu build[/bold] first.")
            raise typer.Exit(code=1)

        # 3. Get programmer
        programmer = get_programmer(programmer_name, console)
        if programmer is None:
            console.print(f"[red]Unknown programmer: {programmer_name!r}[/red]")
            console.print("Supported programmers: avrdude, pk2cmd")
            raise typer.Exit(code=1)

        # 4. Install if needed
        if not programmer.is_cached():
            try:
                programmer.install()
            except RuntimeError as e:
                console.print(f"[bold red]Programmer installation failed:[/bold red] {e}")
                raise typer.Exit(code=1)

        # 5. Flash
        try:
            programmer.flash(hex_file, chip, port=resolved_port, baud=resolved_baud)
        except RuntimeError as e:
            console.print(f"[red]Flash failed:[/red] {e}")
            raise typer.Exit(code=1)

    except typer.Exit:
        raise
    except Exception as e:
        console.print(f"[bold red]Unexpected error:[/bold red] {e}")
        raise typer.Exit(code=1)
