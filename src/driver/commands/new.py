# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

import json
import shutil
import subprocess
import sys
from pathlib import Path
from typing import List, Optional

import questionary
import tomlkit
import typer
from rich.console import Console
from rich.panel import Panel
from rich.prompt import Confirm, Prompt

from ..core.boards import BOARD_CHIPS, BOARD_GROUPS, default_programmer, default_toolchain

console = Console()

# Default CPU frequencies by board name.  ATtinys not listed here default to
# 8 MHz (internal RC oscillator).  The digispark/trinket ship a 16.5 MHz
# crystal used by V-USB, so they get their own entry.
BOARD_FREQUENCIES: dict[str, int] = {
    "arduino_uno":      16_000_000,
    "arduino_nano":     16_000_000,
    "arduino_mega":     16_000_000,
    "arduino_micro":    16_000_000,
    "digispark":        16_500_000,
    "adafruit_trinket": 16_500_000,
}
_DEFAULT_FREQ = 8_000_000

_COMPAT_FLAVORS = ("micropython", "circuitpython")


def _select(message: str, choices: list, default=None):
    """Arrow-key interactive select. Raises typer.Exit on Ctrl-C."""
    answer = questionary.select(message, choices=choices, default=default).ask()
    if answer is None:
        raise typer.Exit(0)
    return answer


def _detect_pkg_manager() -> str | None:
    """Return the first available package manager found in PATH, or None."""
    if shutil.which("uv"):
        return "uv"
    if shutil.which("poetry"):
        return "poetry"
    return None


def get_available_chips() -> List[str]:
    """Dynamically scan the installed pymcu-stdlib package for chip definitions."""
    try:
        import pymcu
        for p in pymcu.__path__:
            chips_dir = Path(p) / "chips"
            if chips_dir.is_dir():
                return sorted(
                    f.stem for f in chips_dir.glob("*.py")
                    if f.name != "__init__.py"
                )
    except ImportError:
        pass
    except Exception as e:
        console.print(f"[yellow]Warning: Could not scan for chips: {e}[/yellow]")
    return []


def _discover_stdlib_flavors() -> List[str]:
    """Return installed pymcu extension packages (pymcu-<flavor>)."""
    try:
        from importlib.metadata import packages_distributions
        flavors = []
        for dist_name in set(
            v for vals in packages_distributions().values() for v in vals
        ):
            if dist_name.startswith("pymcu-") and dist_name != "pymcu-stdlib":
                flavors.append(dist_name[len("pymcu-"):])
        return sorted(flavors)
    except Exception:
        return []


def _chip_imports(chip: str, flavor: str | None) -> str:
    """Generate the import block and a minimal main() body for the given chip
    and optional stdlib flavor.  No star imports."""
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
        imports = f"from pymcu.chips.{chip} import TRISB, PORTB, RB0"
        body = (
            "    TRISB[RB0] = 0\n"
            "    PORTB[RB0] = 1"
        )
    else:
        imports = f"from pymcu.chips.{chip} import PORTB"
        body = "    PORTB[0] = 1"

    return f"{imports}\n\n\ndef main():\n{body}\n"


def new(
    name: str,
    board: Optional[str] = typer.Option(
        None, "--board",
        help="Target development board (e.g. arduino_uno, arduino_nano, arduino_mega).",
    ),
    chip: Optional[str] = typer.Option(
        None, "--chip",
        hidden=True,
        help="Advanced: target MCU chip identifier (bypasses board selection).",
    ),
    freq: Optional[int] = typer.Option(
        None, "--freq",
        hidden=True,
        help="Advanced: target CPU frequency in Hz.",
    ),
    stdlib: Optional[List[str]] = typer.Option(
        None, "--stdlib",
        help="Compat layer to use (micropython or circuitpython). Repeatable.",
    ),
    pkg_manager: Optional[str] = typer.Option(
        None, "--pkg-manager",
        help="Package manager: uv, pip, or poetry.",
    ),
    no_git: bool = typer.Option(False, "--no-git", help="Skip git init."),
    no_src: bool = typer.Option(False, "--no-src", help="Use flat layout instead of src/."),
):
    console.print(Panel(
        f"[bold blue]Scaffolding new pymcu project: [green]{name}[/green][/bold blue]"
    ))

    project_path = Path(name)
    if project_path.exists():
        console.print(f"[red]Error: Directory '{name}' already exists.[/red]")
        raise typer.Exit(code=1)

    # Early frequency validation (CLI-supplied value, applies to advanced mode).
    if freq is not None and freq <= 0:
        console.print("[red]Invalid frequency — must be a positive integer.[/red]")
        raise typer.Exit(code=1)

    # ------------------------------------------------------------------
    # Mode detection
    # ------------------------------------------------------------------
    # advanced_mode: --chip was explicitly supplied (hidden flag).
    # Standard mode (default): board-first compat flow.
    advanced_mode = chip is not None

    if not advanced_mode:
        # ── Standard compat flow ──────────────────────────────────────

        # 1. Compat flavor — mandatory (micropython or circuitpython)
        if stdlib is None:
            discovered = _discover_stdlib_flavors()
            compat_options = [f for f in discovered if f in _COMPAT_FLAVORS]
            if not compat_options:
                compat_options = list(_COMPAT_FLAVORS)
            flavor_choice = _select("Compatibility layer:", compat_options, default=compat_options[0])
            stdlib = [flavor_choice]

        # 2. Board selection — two-level: manufacturer → board
        if board is None:
            manufacturers = list(BOARD_GROUPS.keys())
            mfr = _select("Manufacturer:", manufacturers)

            board_keys = BOARD_GROUPS[mfr]
            board_choices = [
                questionary.Choice(
                    title=f"{k:<22}  ({BOARD_CHIPS[k]}, {BOARD_FREQUENCIES.get(k, _DEFAULT_FREQ) // 1_000_000} MHz)",
                    value=k,
                )
                for k in board_keys
            ]
            board = _select(f"Board:", board_choices)

        chip = BOARD_CHIPS.get(board)
        if chip is None:
            console.print(
                f"[red]Unknown board '{board}'. "
                "Use --chip to specify a custom target.[/red]"
            )
            raise typer.Exit(code=1)
        freq = BOARD_FREQUENCIES.get(board, _DEFAULT_FREQ)

    else:
        # ── Advanced chip mode (hidden --chip flag) ───────────────────

        if freq is None:
            raw = Prompt.ask("Target frequency (Hz)", default="4000000")
            try:
                freq = int(raw.replace("_", "").replace(",", ""))
                if freq <= 0:
                    raise ValueError
            except ValueError:
                console.print("[red]Invalid frequency — must be a positive integer.[/red]")
                raise typer.Exit(code=1)

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
            stdlib = (
                [flavor_choice]
                if flavor_choice and flavor_choice != none_label
                else []
            )

    # ------------------------------------------------------------------
    # Package manager — auto-detect, then ask if none found
    # ------------------------------------------------------------------
    if pkg_manager is None:
        detected = _detect_pkg_manager()
        if detected:
            console.print(f"[dim]Detected package manager: {detected}[/dim]")
            pkg_manager = detected
        else:
            console.print(
                "[yellow]No package manager (uv or poetry) found in PATH.[/yellow]"
            )
            install_uv = questionary.confirm("Install uv? (recommended)").ask()
            if install_uv:
                with console.status("[bold green]Installing uv via pip..."):
                    result = subprocess.run(
                        [sys.executable, "-m", "pip", "install", "uv"],
                        capture_output=True,
                    )
                if result.returncode == 0:
                    pkg_manager = "uv"
                else:
                    console.print(
                        "[yellow]uv installation failed, falling back to pip.[/yellow]"
                    )
                    pkg_manager = "pip"
            else:
                pkg_manager = "pip"

    # ------------------------------------------------------------------
    # Layout
    # ------------------------------------------------------------------
    use_src = not no_src
    sources_dir = "src" if use_src else "."
    entry_file = "main.py" if use_src else "app.py"

    # ------------------------------------------------------------------
    # Toolchain + programmer
    # ------------------------------------------------------------------
    # Derive toolchain name from chip prefix — do not rely on plugins being
    # installed at scaffold time (the user may not have pymcu[avr] yet).
    toolchain_name = default_toolchain(chip)
    programmer_name = default_programmer(chip)

    def _pin_version(pkg_name: str, fallback: str) -> str:
        try:
            from importlib.metadata import version
            return f"{pkg_name}>={version(pkg_name)}"
        except Exception:
            return fallback

    # Compiler driver + backend extra. The PyPI package is `pymcu-compiler`
    # (the `pymcuc` binary it ships is NOT a distribution); installing it with
    # the backend extra (e.g. [avr]) pulls the codegen backend and toolchain so
    # a fresh `pip install` of the generated project is self-contained.
    _chip_lower = chip.lower()
    if _chip_lower.startswith("at"):
        compiler_extra = "[avr]"
    elif _chip_lower == "rp2040":
        compiler_extra = "[arm]"
    else:
        compiler_extra = ""

    def _pin_compiler() -> str:
        try:
            from importlib.metadata import version
            return f"pymcu-compiler{compiler_extra}>={version('pymcu-compiler')}"
        except Exception:
            return f"pymcu-compiler{compiler_extra}"

    # ------------------------------------------------------------------
    # File generation
    # ------------------------------------------------------------------
    try:
        project_path.mkdir(parents=True)
        if use_src:
            (project_path / sources_dir).mkdir(parents=True)

        primary_flavor = stdlib[0] if stdlib else None
        main_content = _chip_imports(chip, primary_flavor)

        # ── pyproject.toml ────────────────────────────────────────────
        doc = tomlkit.document()

        if pkg_manager in ("uv", "poetry", "pip"):
            project_tbl = tomlkit.table()
            project_tbl.add("name", name)
            project_tbl.add("version", "0.1.0")

            deps = tomlkit.array()
            deps.append(_pin_version("pymcu-stdlib", "pymcu-stdlib"))
            deps.append(_pin_compiler())
            for flavor in stdlib:
                deps.append(_pin_version(f"pymcu-{flavor}", f"pymcu-{flavor}"))
            project_tbl.add("dependencies", deps)
            doc.add("project", project_tbl)

        pymcu_tool = tomlkit.table()
        if not advanced_mode:
            pymcu_tool.add("board", board)   # target is derived from board in build.py
        else:
            pymcu_tool.add("target", chip)   # advanced mode: chip set directly, no board
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

        # ── requirements.txt (pip only) ───────────────────────────────
        if pkg_manager == "pip":
            lines = [
                _pin_version("pymcu-stdlib", "pymcu-stdlib"),
                _pin_compiler(),
            ]
            for flavor in stdlib:
                lines.append(_pin_version(f"pymcu-{flavor}", f"pymcu-{flavor}"))
            with open(project_path / "requirements.txt", "w") as f:
                f.write("\n".join(lines) + "\n")

        # ── Makefile ──────────────────────────────────────────────────
        if pkg_manager == "uv":
            makefile_content = ".PHONY: sync\n\nsync:\n\tuv sync && pymcu sync\n"
        elif pkg_manager == "poetry":
            makefile_content = ".PHONY: install\n\ninstall:\n\tpoetry install && pymcu sync\n"
        else:
            makefile_content = (
                ".PHONY: install\n\n"
                "install:\n"
                "\tpip install -r requirements.txt && pymcu sync\n"
            )
        # newline="\n": keep LF so the Makefile stays valid on macOS/Linux/WSL even
        # when generated on Windows (Python text mode would otherwise write CRLF).
        with open(project_path / "Makefile", "w", newline="\n") as f:
            f.write(makefile_content)

        # ── VS Code tasks ─────────────────────────────────────────────
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
                    "label": "pymcu: sync",
                    "type": "shell",
                    "command": "pymcu sync",
                    "runOptions": {"runOn": "folderOpen"},
                    "problemMatcher": [],
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

        # ── .gitignore ────────────────────────────────────────────────
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

        # ── Entry point ───────────────────────────────────────────────
        entry_dir = project_path / sources_dir if use_src else project_path
        with open(entry_dir / entry_file, "w") as f:
            f.write(main_content)

        # ── Git init + hooks ──────────────────────────────────────────
        git_inited = False
        if not no_git and questionary.confirm("Initialize git repository?").ask():
            try:
                subprocess.run(
                    ["git", "init"], cwd=project_path, check=True, capture_output=True
                )
                git_inited = True
            except subprocess.CalledProcessError as e:
                console.print(f"[red]Failed to initialize git repository:[/red] {e}")

        if git_inited:
            hooks_dir = project_path / ".git" / "hooks"
            hook_script = (
                "#!/bin/sh\n"
                "# Regenerate board shims if pyproject.toml changed.\n"
                "git diff --name-only HEAD@{1} HEAD 2>/dev/null"
                " | grep -q 'pyproject.toml' && pymcu sync\n"
            )
            for hook_name in ("post-merge", "post-checkout"):
                hook_file = hooks_dir / hook_name
                # newline="\n": Git for Windows runs hooks through its bundled sh, which
                # needs the "#!/bin/sh" shebang on an LF line. Python text mode would
                # translate "\n" to CRLF on Windows and break the shebang.
                hook_file.write_text(hook_script, encoding="utf-8", newline="\n")
                # chmod's executable bit is meaningless on Windows (Git uses the shebang),
                # so only set it where it matters.
                if sys.platform != "win32":
                    hook_file.chmod(0o755)

        # ── Install dependencies ──────────────────────────────────────
        if questionary.confirm(f"Install dependencies with {pkg_manager} now?").ask():
            with console.status(
                f"[bold green]Installing dependencies via {pkg_manager}..."
            ):
                if pkg_manager == "uv":
                    subprocess.run(["uv", "sync"], cwd=project_path, check=True)
                elif pkg_manager == "poetry":
                    subprocess.run(
                        ["poetry", "install"], cwd=project_path, check=True
                    )
                elif pkg_manager == "pip":
                    subprocess.run(
                        [sys.executable, "-m", "venv", ".venv"], cwd=project_path
                    )
                    # The venv layout differs by platform: Scripts/python.exe on
                    # Windows, bin/python elsewhere. Invoke pip via "python -m pip" so
                    # we don't depend on the exact pip executable name either.
                    if sys.platform == "win32":
                        venv_python = project_path / ".venv" / "Scripts" / "python.exe"
                    else:
                        venv_python = project_path / ".venv" / "bin" / "python"
                    pip_cmd = [
                        str(venv_python),
                        "-m", "pip",
                        "install", "-r", "requirements.txt",
                    ]
                    subprocess.run(pip_cmd, cwd=project_path, check=True)

        # ── Summary ───────────────────────────────────────────────────
        console.print(
            f"[bold green]+[/bold green] Project '[bold]{name}[/bold]' created successfully!"
        )
        if not advanced_mode:
            console.print(f"[blue]Board:[/blue]          {board}")
        console.print(f"[blue]Target MCU:[/blue]     {chip}")
        console.print(f"[blue]Frequency:[/blue]      {freq:,} Hz")
        console.print(f"[blue]Toolchain:[/blue]      {toolchain_name}")
        console.print(f"[blue]Programmer:[/blue]     {programmer_name}")
        console.print(f"[blue]Package Mgr:[/blue]    {pkg_manager}")
        if stdlib:
            console.print(f"[blue]stdlib:[/blue]         {', '.join(stdlib)}")
        console.print("[dim]VS Code tasks created in .vscode/tasks.json[/dim]")
        if sys.platform == "win32":
            console.print(
                "[dim]Windows: 'make' is not preinstalled — run [bold]pymcu sync[/bold] "
                "directly instead of the Makefile.[/dim]"
            )

    except typer.Exit:
        raise
    except Exception as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(code=1)
