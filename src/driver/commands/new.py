# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

from pathlib import Path
import typer
from typing import Optional, List
import tomlkit
import json
import subprocess
import sys
from rich.console import Console
from rich.panel import Panel
from rich.prompt import Prompt, Confirm

from ..toolchains import get_toolchain_for_chip
from ..core.boards import default_programmer

console = Console()


def get_available_chips() -> List[str]:
    """
    Dynamically scans the installed 'pymcu-stdlib' package for chip definitions.
    Returns a list of chip names (e.g., ['pic16f84a', 'pic16f877a']).
    """
    try:
        import pymcu
        for p in pymcu.__path__:
            chips_dir = Path(p) / "chips"
            if chips_dir.is_dir():
                chips = [
                    f.stem for f in chips_dir.glob("*.py")
                    if f.name != "__init__.py"
                ]
                return sorted(chips)
    except ImportError:
        pass
    except Exception as e:
        console.print(f"[yellow]Warning: Could not scan for chips: {e}[/yellow]")
    return []


def _discover_stdlib_flavors() -> List[str]:
    """
    Return a list of installed pymcu extension packages (pymcu-<flavor>).
    Scans importlib.metadata for installed packages starting with 'pymcu-'.
    """
    try:
        from importlib.metadata import packages_distributions
        flavors = []
        for dist_name in set(
            v for vals in packages_distributions().values() for v in vals
        ):
            if dist_name.startswith("pymcu-") and dist_name != "pymcu-stdlib":
                flavor = dist_name[len("pymcu-"):]
                flavors.append(flavor)
        return sorted(flavors)
    except Exception:
        return []


def _chip_imports(chip: str, flavor: str | None) -> str:
    """
    Generate the import block and a minimal main() body for the given chip
    and optional stdlib flavor.  No star imports — only the symbols actually
    used in the template are imported explicitly.
    """
    chip_lower = chip.lower()
    is_avr = chip_lower.startswith("at")
    is_pic = chip_lower.startswith("pic")

    if flavor == "micropython":
        imports = "from machine import Pin"
        body = (
            "    led = Pin(13, Pin.OUT)\n"
            "    while True:\n"
            "        led.value(1)\n"
            "        led.value(0)"
        )
    elif flavor == "circuitpython":
        imports = "import board\nimport digitalio"
        body = (
            "    led = digitalio.DigitalInOut(board.LED)\n"
            "    led.direction = digitalio.Direction.OUTPUT\n"
            "    while True:\n"
            "        led.value = True\n"
            "        led.value = False"
        )
    elif is_avr:
        imports = (
            f"from pymcu.chips.{chip} import DDRB, PORTB, DDB5, PORTB5\n"
            "from pymcu.time import delay_ms"
        )
        body = (
            "    DDRB[DDB5] = 1\n"
            "    while True:\n"
            "        PORTB[PORTB5] = 1\n"
            "        delay_ms(500)\n"
            "        PORTB[PORTB5] = 0\n"
            "        delay_ms(500)"
        )
    elif is_pic:
        imports = (
            f"from pymcu.chips.{chip} import TRISB, PORTB, RB0"
        )
        body = (
            "    TRISB[RB0] = 0\n"
            "    PORTB[RB0] = 1"
        )
    else:
        imports = f"from pymcu.chips.{chip} import PORTB"
        body = "    PORTB[0] = 1"

    return (
        f"{imports}\n\n\n"
        f"def main():\n"
        f"{body}\n"
    )


def new(
    name: str,
    chip: Optional[str] = typer.Option(None, "--chip", help="Target MCU chip identifier."),
    freq: Optional[int] = typer.Option(None, "--freq", help="Target CPU frequency in Hz."),
    stdlib: Optional[List[str]] = typer.Option(
        None, "--stdlib",
        help="stdlib flavor to add (e.g. micropython). Repeatable.",
    ),
    pkg_manager: Optional[str] = typer.Option(
        None, "--pkg-manager",
        help="Package manager: uv, pip, or poetry.",
    ),
    no_git: bool = typer.Option(False, "--no-git", help="Skip git init."),
    no_src: bool = typer.Option(False, "--no-src", help="Use flat layout instead of src/."),
):
    console.print(Panel(f"[bold blue]Scaffolding new pymcu project: [green]{name}[/green][/bold blue]"))

    project_path = Path(name)
    if project_path.exists():
        console.print(f"[red]Error: Directory '{name}' already exists.[/red]")
        raise typer.Exit(code=1)

    # ── Chip selection ────────────────────────────────────────────────────────
    if chip is None:
        available_chips = get_available_chips()
        if available_chips:
            chip = Prompt.ask("Target MCU", choices=available_chips, default="pic16f84a")
        else:
            chip = Prompt.ask("Target MCU", default="pic16f84a")

    # ── Frequency ─────────────────────────────────────────────────────────────
    if freq is None:
        raw = Prompt.ask("Target frequency (Hz)", default="4000000")
        try:
            freq = int(raw.replace("_", "").replace(",", ""))
            if freq <= 0:
                raise ValueError
        except ValueError:
            console.print("[red]Invalid frequency — must be a positive integer.[/red]")
            raise typer.Exit(code=1)

    # ── stdlib flavors ────────────────────────────────────────────────────────
    if stdlib is None:
        discovered = _discover_stdlib_flavors()
        if discovered:
            console.print(
                f"[dim]Installed stdlib flavors: {', '.join(discovered)}[/dim]"
            )
        none_label = "none"
        flavor_choice = Prompt.ask(
            "stdlib flavor (none / micropython / circuitpython / ...)",
            default=none_label,
        )
        stdlib = [flavor_choice] if flavor_choice and flavor_choice != none_label else []

    # ── Package manager ───────────────────────────────────────────────────────
    if pkg_manager is None:
        pkg_manager = Prompt.ask(
            "Which package manager would you like to use?",
            choices=["uv", "pip", "poetry"],
            default="uv",
        )

    # ── Layout ────────────────────────────────────────────────────────────────
    use_src = not no_src
    sources_dir = "src" if use_src else "."
    entry_file = "main.py" if use_src else "app.py"

    # ── Toolchain detection ───────────────────────────────────────────────────
    try:
        toolchain_instance = get_toolchain_for_chip(chip, console)
        toolchain_name = toolchain_instance.get_name()
    except ValueError:
        console.print(f"[yellow]Warning: No specific toolchain known for '{chip}'. Defaulting to 'gputils'.[/yellow]")
        toolchain_name = "gputils"

    # ── Programmer selection ──────────────────────────────────────────────────
    programmer_name = default_programmer(chip)

    # ── Pin versions for dependency reproducibility ───────────────────────────
    def _pin_version(pkg_name: str, fallback: str) -> str:
        try:
            from importlib.metadata import version
            ver = version(pkg_name)
            return f"{pkg_name}>={ver}"
        except Exception:
            return fallback

    try:
        project_path.mkdir(parents=True)
        if use_src:
            (project_path / sources_dir).mkdir(parents=True)

        # ── Select template flavor ────────────────────────────────────────────
        primary_flavor = stdlib[0] if stdlib else None
        main_content = _chip_imports(chip, primary_flavor)

        # ── pyproject.toml ────────────────────────────────────────────────────
        doc = tomlkit.document()

        if pkg_manager in ("uv", "poetry", "pip"):
            project_tbl = tomlkit.table()
            project_tbl.add("name", name)
            project_tbl.add("version", "0.1.0")

            deps = tomlkit.array()
            deps.append(_pin_version("pymcu-stdlib", "pymcu-stdlib"))
            deps.append(_pin_version("pymcuc", "pymcuc"))
            for flavor in stdlib:
                deps.append(_pin_version(f"pymcu-{flavor}", f"pymcu-{flavor}"))
            project_tbl.add("dependencies", deps)
            doc.add("project", project_tbl)

        pymcu_tool = tomlkit.table()
        pymcu_tool.add("target", chip)
        pymcu_tool.add("frequency", freq)
        pymcu_tool.add("sources", sources_dir)
        pymcu_tool.add("entry", entry_file)

        if stdlib:
            stdlib_arr = tomlkit.array()
            for f in stdlib:
                stdlib_arr.append(f)
            pymcu_tool.add("stdlib", stdlib_arr)

        pymcu_tool.add("config", tomlkit.table())

        pymcu_toolchain = tomlkit.table()
        pymcu_toolchain.add("name", toolchain_name)
        pymcu_tool.add("toolchain", pymcu_toolchain)

        pymcu_programmer = tomlkit.table()
        pymcu_programmer.add("name", programmer_name)
        pymcu_tool.add("programmer", pymcu_programmer)

        if "tool" not in doc:
            doc.add("tool", tomlkit.table())
        doc["tool"].add("pymcu", pymcu_tool)

        with open(project_path / "pyproject.toml", "w") as f:
            f.write(tomlkit.dumps(doc))

        if pkg_manager == "pip":
            requirements_content = _pin_version("pymcu-stdlib", "pymcu-stdlib") + "\n"
            requirements_content += _pin_version("pymcuc", "pymcuc") + "\n"
            for flavor in stdlib:
                requirements_content += _pin_version(f"pymcu-{flavor}", f"pymcu-{flavor}") + "\n"
            with open(project_path / "requirements.txt", "w") as f:
                f.write(requirements_content)

        # ── VS Code Tasks ─────────────────────────────────────────────────────
        vscode_dir = project_path / ".vscode"
        vscode_dir.mkdir()
        tasks_json = {
            "version": "2.0.0",
            "tasks": [
                {
                    "label": "pymcu: build",
                    "type": "shell",
                    "command": "pymcu build",
                    "group": {"kind": "build", "isDefault": True},
                    "problemMatcher": ["$pymcuc"],
                },
                {
                    "label": "pymcu: clean",
                    "type": "shell",
                    "command": "pymcu clean",
                    "problemMatcher": [],
                },
                {
                    "label": "pymcu: flash",
                    "type": "shell",
                    "command": "pymcu flash",
                    "problemMatcher": [],
                },
            ],
        }
        with open(vscode_dir / "tasks.json", "w") as f:
            json.dump(tasks_json, f, indent=4)

        # ── .gitignore ────────────────────────────────────────────────────────
        gitignore_content = (
            "__pycache__/\n"
            "dist/\n"
            "*.hex\n"
            "*.cod\n"
            "*.lst\n"
            ".venv/\n"
            ".vscode/settings.json\n"
        )
        with open(project_path / ".gitignore", "w") as f:
            f.write(gitignore_content)

        # ── Entry point ───────────────────────────────────────────────────────
        entry_dir = project_path / sources_dir if use_src else project_path
        with open(entry_dir / entry_file, "w") as f:
            f.write(main_content)

        # ── Git init ──────────────────────────────────────────────────────────
        if not no_git and Confirm.ask("Initialize git repository?", default=True):
            try:
                subprocess.run(["git", "init"], cwd=project_path, check=True, capture_output=True)
            except subprocess.CalledProcessError as e:
                console.print(f"[red]Failed to initialize git repository:[/red] {e}")

        # ── Install dependencies ──────────────────────────────────────────────
        if Confirm.ask(f"Install dependencies with {pkg_manager} now?", default=True):
            with console.status(f"[bold green]Installing dependencies via {pkg_manager}..."):
                if pkg_manager == "uv":
                    subprocess.run(["uv", "sync"], cwd=project_path, check=True)
                elif pkg_manager == "poetry":
                    subprocess.run(["poetry", "install"], cwd=project_path, check=True)
                elif pkg_manager == "pip":
                    subprocess.run([sys.executable, "-m", "venv", ".venv"], cwd=project_path)
                    pip_cmd = [
                        str(project_path / ".venv" / "bin" / "pip"),
                        "install", "-r", "requirements.txt",
                    ]
                    subprocess.run(pip_cmd, cwd=project_path, check=True)

        console.print(f"[bold green]+[/bold green] Project '[bold]{name}[/bold]' created successfully!")
        console.print(f"[blue]Target MCU:[/blue]     {chip}")
        console.print(f"[blue]Frequency:[/blue]      {freq:,} Hz")
        console.print(f"[blue]Toolchain:[/blue]      {toolchain_name}")
        console.print(f"[blue]Programmer:[/blue]     {programmer_name}")
        console.print(f"[blue]Package Mgr:[/blue]    {pkg_manager}")
        if stdlib:
            console.print(f"[blue]stdlib:[/blue]         {', '.join(stdlib)}")
        console.print("[dim]VS Code tasks created in .vscode/tasks.json[/dim]")

    except typer.Exit:
        raise
    except Exception as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(code=1)

