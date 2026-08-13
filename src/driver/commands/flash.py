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

from pathlib import Path
from typing import Optional
import tomlkit
import typer
from rich.console import Console
from ..programmers import get_programmer
from ..core.boards import BOARD_CHIPS, default_programmer, firmware_artifacts

console = Console()


def _default_programmer(chip: str) -> str:
    return default_programmer(chip)


def _artifact_candidates(programmer, chip: str) -> tuple[str, ...]:
    """Return the dist/ filenames to look for, most preferred first.

    A programmer plugin may declare its own by exposing a ``firmware_artifacts``
    attribute (a sequence of filenames, or a callable taking the chip id).
    Otherwise the target family decides: HEX for AVR/PIC, .uf2/.bin for the RP
    targets.
    """
    declared = getattr(programmer, "firmware_artifacts", None)
    if callable(declared):
        declared = declared(chip)
    if declared:
        return tuple(declared)
    return firmware_artifacts(chip)


def flash(
    verbose: bool = typer.Option(False, "--verbose", "-v", help="Enable verbose logging"),
    port: Optional[str] = typer.Option(
        None, "--port", "-P",
        help="Serial port for flashing (e.g. COM3 on Windows, /dev/cu.usbmodemXXXX on "
             "macOS, /dev/ttyACM0 on Linux). "
             "Overrides \\[tool.pymcu.flash].port in pyproject.toml.",
    ),
):
    """
    Flashes the built firmware to the target microcontroller.

    Port resolution order:
      1. --port / -P CLI argument
      2. port = "..." in \\[tool.pymcu.flash] of pyproject.toml
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
                "[red]No 'target' or 'board' specified in \\[tool.pymcu] of pyproject.toml.[/red]"
            )
            raise typer.Exit(code=1)

        flash_config = pymcu_config.get("flash", {})
        # [tool.pymcu.programmer] name = "..." is the pre-0.15 spelling that
        # `pymcu new` used to scaffold; honour it so those projects keep working.
        legacy_config = pymcu_config.get("programmer", {})
        legacy_name = legacy_config.get("name") if legacy_config else None
        if legacy_name and not flash_config.get("programmer"):
            # Square brackets are rich markup: escape the TOML headers.
            console.print(
                "[yellow]Deprecated:[/yellow] \\[tool.pymcu.programmer] is read only as "
                "a fallback. Move it to:\n"
                f"  [dim]\\[tool.pymcu.flash]\n  programmer = \"{legacy_name}\"[/dim]"
            )

        programmer_name = (
            flash_config.get("programmer") or legacy_name or _default_programmer(chip)
        )
        cfg_port = flash_config.get("port")
        cfg_baud = flash_config.get("baud")

        # CLI --port takes priority over pyproject.toml
        resolved_port: str | None = port or cfg_port or None
        resolved_baud: int | None = int(cfg_baud) if cfg_baud else None

        # 2. Get programmer (entry-point plugins first, then built-ins)
        programmer = get_programmer(programmer_name, console)
        if programmer is None:
            console.print(f"[red]Unknown programmer: {programmer_name!r}[/red]")
            console.print("Supported programmers: avrdude, pk2cmd")
            raise typer.Exit(code=1)

        # 3. Locate the firmware artifact this target/programmer flashes from
        dist_dir = Path("dist")
        candidates = _artifact_candidates(programmer, chip)
        artifact = next(
            (dist_dir / n for n in candidates if (dist_dir / n).exists()), None
        )
        if artifact is None:
            expected = " or ".join(f"'dist/{n}'" for n in candidates)
            console.print(f"[red]Firmware file {expected} not found.[/red]")
            console.print("Please run [bold]pymcu build[/bold] first.")
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
            programmer.flash(artifact, chip, port=resolved_port, baud=resolved_baud)
        except (RuntimeError, OSError) as e:
            console.print(f"[red]Flash failed:[/red] {e}")
            raise typer.Exit(code=1)

    except typer.Exit:
        raise
    except Exception as e:
        console.print(f"[bold red]Unexpected error:[/bold red] {e}")
        raise typer.Exit(code=1)
