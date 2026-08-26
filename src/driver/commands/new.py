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
from rich.prompt import Prompt

from ..core.boards import (
    BOARD_CHIPS,
    BOARD_GROUPS,
    board_frequency,
    default_frequency,
    default_programmer,
    default_toolchain,
    suggest_boards,
)

console = Console()

_COMPAT_FLAVORS = ("micropython", "circuitpython")


def _interactive() -> bool:
    """
    True when there is a real terminal on both ends.

    stdin alone is not enough: prompt_toolkit also opens the output console, and
    on Windows it raises "No Windows console found" when stdout is redirected
    even though stdin is a tty. Note that NUL is a character device there, so
    `< NUL` does not make stdin look redirected either -- the output side is
    what actually decides.
    """
    try:
        return sys.stdin.isatty() and sys.stdout.isatty()
    except (AttributeError, ValueError):   # closed or replaced streams
        return False


def _select(message: str, choices: list, default=None, *, flag: str = ""):
    """
    Arrow-key interactive select. Raises typer.Exit on Ctrl-C.

    Without a terminal there is no safe answer to invent -- picking a board or
    an MCU on the user's behalf would silently scaffold the wrong target -- so
    this asks for the equivalent flag instead. Every call site runs before any
    file is written, so failing here leaves nothing behind.
    """
    if not _interactive():
        hint = f" Pass {flag} on the command line." if flag else ""
        console.print(f"[red]Cannot prompt for '{message}' without a terminal.[/red]{hint}")
        raise typer.Exit(code=1)

    try:
        answer = questionary.select(message, choices=choices, default=default).ask()
    except Exception as e:
        hint = f" Pass {flag} on the command line." if flag else ""
        console.print(f"[red]Interactive prompt unavailable:[/red] {e}{hint}")
        raise typer.Exit(code=1)

    if answer is None:
        raise typer.Exit(0)
    return answer


def _confirm(message: str, default: bool = False, *, non_interactive: bool | None = None) -> bool:
    """
    Y/N confirm via questionary.

    Without a terminal it answers `non_interactive` (falling back to `default`)
    instead of raising: these questions are all optional extras, and the project
    is already on disk by the time they are asked. A prompt that blows up here
    used to abort the command mid-scaffold.
    """
    if non_interactive is None:
        non_interactive = default
    if not _interactive():
        return non_interactive

    try:
        answer = questionary.confirm(message, default=default).ask()
    except Exception:
        # prompt_toolkit can still fail on a console it cannot drive; treat it
        # the same as having no terminal rather than losing the scaffold.
        return non_interactive

    return bool(answer) if answer is not None else default


def _resolve_uv() -> str | None:
    """Absolute path to a runnable `uv`, or None.

    PATH alone is not enough. When PyMCU runs from a pipx venv, `pip install uv`
    puts the binary in that venv's bin/ and pipx exposes only the app's declared
    entry points, so `uv` never reaches PATH. Look there too, and let the `uv`
    package point at its own binary if it can.
    """
    found = shutil.which("uv")
    if found:
        return found

    exe = "uv.exe" if sys.platform == "win32" else "uv"
    candidate = Path(sys.executable).parent / exe
    if candidate.is_file():
        return str(candidate)

    try:
        from uv import find_uv_bin  # type: ignore

        located = Path(find_uv_bin())
        if located.is_file():
            return str(located)
    except Exception:
        pass

    return None


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


# Floor used when a package cannot be inspected locally. It names a prerelease
# on purpose: pip only considers prereleases for a requirement when the
# specifier itself mentions one (or when nothing stable exists at all), so a
# bare name would make the generated project need `pip install --pre` the day
# any of these packages ships a stable release.
_PRERELEASE_FLOOR = "0.1.0a1"


def _pin(pkg_name: str, extra: str = "") -> str:
    """
    Requirement line for *pkg_name*, pinned to what is installed here.

    The version is read from the environment running the CLI, which is not the
    environment the generated project will use. Under pipx that gap is the
    normal case: the CLI lives in its own venv and the stdlib flavors are not
    among its dependencies, so they are simply not importable from here.
    """
    try:
        from importlib.metadata import version
        return f"{pkg_name}{extra}>={version(pkg_name)}"
    except Exception:
        return f"{pkg_name}{extra}>={_PRERELEASE_FLOOR}"


def _chip_imports(chip: str, flavor: str | None) -> str:
    """Generate a minimal blink program for the given chip and stdlib flavor.

    The compat flavors get a top-level script, because that is how MicroPython
    and CircuitPython code is actually written -- main.py and code.py run at
    module level, and every published snippet a newcomer will paste in looks
    like that, including this project's own docs/compat pages. Wrapping the
    scaffold in `def main():` made "replace the contents with your program"
    misleading: the obvious move produces a file whose indentation no longer
    matches. The native register-level targets keep `def main():`, which is
    the shape their examples and the test fixtures use.
    """
    chip_lower = chip.lower()
    is_avr = chip_lower.startswith("at")
    is_pic = chip_lower.startswith("pic")

    if flavor == "micropython":
        imports = "from machine import Pin\nfrom time import sleep_ms"
        body = (
            "led = Pin(13, Pin.OUT)\n"
            "while True:\n"
            "    led.value(1)\n"
            "    sleep_ms(500)\n"
            "    led.value(0)\n"
            "    sleep_ms(500)"
        )
    elif flavor == "circuitpython":
        imports = "import board\nimport digitalio\nimport time"
        body = (
            "led = digitalio.DigitalInOut(board.LED)\n"
            "led.direction = digitalio.Direction.OUTPUT\n"
            "while True:\n"
            "    led.value = True\n"
            "    time.sleep(0.5)\n"
            "    led.value = False\n"
            "    time.sleep(0.5)"
        )
    elif is_avr:
        imports = (
            f"from pymcu.chips.{chip} import DDRB, PORTB, DDB5, PORTB5\n"
            "from pymcu.time import delay_ms"
        )
        body = (
            "DDRB[DDB5] = 1\n"
            "while True:\n"
            "    PORTB[PORTB5] = 1\n"
            "    delay_ms(500)\n"
            "    PORTB[PORTB5] = 0\n"
            "    delay_ms(500)"
        )
    elif is_pic:
        imports = f"from pymcu.chips.{chip} import TRISB, PORTB, RB0"
        body = (
            "TRISB[RB0] = 0\n"
            "PORTB[RB0] = 1"
        )
    else:
        imports = f"from pymcu.chips.{chip} import PORTB"
        body = "PORTB[0] = 1"

    if flavor in ("micropython", "circuitpython"):
        return f"{imports}\n\n{body}\n"

    indented = "\n".join(f"    {line}" if line else "" for line in body.split("\n"))
    return f"{imports}\n\n\ndef main():\n{indented}\n"


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
            flavor_choice = _select("Compatibility layer:", compat_options, default=compat_options[0], flag="--stdlib")
            stdlib = [flavor_choice]

        # 2. Board selection — two-level: manufacturer → board
        if board is None:
            manufacturers = list(BOARD_GROUPS.keys())
            mfr = _select("Manufacturer:", manufacturers, flag="--board")

            board_keys = BOARD_GROUPS[mfr]
            board_choices = [
                questionary.Choice(
                    title=f"{k:<22}  ({BOARD_CHIPS[k]}, {board_frequency(k) // 1_000_000} MHz)",
                    value=k,
                )
                for k in board_keys
            ]
            board = _select(f"Board:", board_choices, flag="--board")

        # BOARD_CHIPS alone, which is what this command scaffolds from: a board a compat
        # layer declares can have no toolchain behind it, and `pymcu new` accepting one would
        # produce a project whose first build says "No toolchain found for chip 'stm32f405'".
        # The suggestion is computed over the same table, so it never offers a name this
        # command would then refuse.
        chip = BOARD_CHIPS.get(board)
        if chip is None:
            near = suggest_boards(board)
            hint = (f" Did you mean '{near[0]}'?" if len(near) == 1
                    else f" Close names: {', '.join(near)}." if near
                    else "")
            console.print(
                f"[red]Unknown board '{board}'.{hint}[/red]\n"
                "  [dim]`pymcu boards` lists what this installation supports.[/dim]\n"
                "  [dim]--chip names a bare target instead of a board.[/dim]"
            )
            raise typer.Exit(code=1)
        freq = board_frequency(board)

    else:
        # ── Advanced chip mode (hidden --chip flag) ───────────────────

        if freq is None:
            raw = Prompt.ask(
                "Target frequency (Hz)", default=str(default_frequency(chip))
            )
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
    # Ruta absoluta a uv, resuelta una sola vez: no basta con el nombre, ver
    # _resolve_uv(). None mientras no se sepa que hay uv utilizable.
    uv_bin: str | None = None

    if pkg_manager is None:
        detected = _detect_pkg_manager()
        if detected:
            console.print(f"[dim]Detected package manager: {detected}[/dim]")
            pkg_manager = detected
            if detected == "uv":
                uv_bin = _resolve_uv()
        else:
            console.print(
                "[yellow]No package manager (uv or poetry) found in PATH.[/yellow]"
            )
            if _confirm("Install uv? (recommended)", default=True):
                with console.status("[bold green]Installing uv via pip..."):
                    result = subprocess.run(
                        [sys.executable, "-m", "pip", "install", "uv"],
                        capture_output=True,
                    )
                # A zero exit code is NOT enough to conclude that `uv` can be
                # run. Installed from a pipx venv it lands in that venv's bin/,
                # which pipx does not expose on PATH -- only the app's own entry
                # points get shims -- so `subprocess.run(["uv", ...])` later
                # dies with ENOENT. Resolve the real executable and keep it.
                if result.returncode == 0:
                    uv_bin = _resolve_uv()
                    if uv_bin:
                        pkg_manager = "uv"
                    else:
                        console.print(
                            "[yellow]uv installed but its executable could not be located; "
                            "falling back to pip.[/yellow]"
                        )
                        pkg_manager = "pip"
                else:
                    console.print(
                        "[yellow]uv installation failed, falling back to pip.[/yellow]"
                    )
                    pkg_manager = "pip"
            else:
                pkg_manager = "pip"

    # Tambien cuando llega por --pkg-manager uv: ahi no pasamos por la deteccion.
    if pkg_manager == "uv" and uv_bin is None:
        uv_bin = _resolve_uv()

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

    def _pin_version(pkg_name: str, fallback: str = "") -> str:
        return _pin(pkg_name)

    # Compiler driver + backend extra. The PyPI package is `pymcu-compiler`
    # (the `pymcuc` binary it ships is NOT a distribution); installing it with
    # the backend extra (e.g. [avr]) pulls the codegen backend and toolchain so
    # a fresh `pip install` of the generated project is self-contained.
    _chip_lower = chip.lower()
    if _chip_lower.startswith("at"):
        compiler_extra = "[avr]"
    elif _chip_lower in ("rp2040", "rp2350"):
        compiler_extra = "[arm]"
    elif _chip_lower.startswith("pic"):
        compiler_extra = "[pic]"
    else:
        # No riscv extra exists yet -- see the note in pymcu-compiler's
        # pyproject.toml. An extra that cannot resolve fails harder than a
        # missing one, and `pymcu build` already prints the install command.
        compiler_extra = ""

    def _pin_compiler() -> str:
        return _pin("pymcu-compiler", extra=compiler_extra)

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
            # Without this uv picks its own floor (>=3.12 today) and the project
            # refuses to sync on the 3.11 the docs say is supported.
            project_tbl.add("requires-python", ">=3.11")

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

        # [tool.pymcu.config] son los bits de configuracion del chip (los config
        # words de PIC: FOSC, WDTE...), que el driver pasa al compilador como
        # --config CLAVE=VALOR. En AVR no se usa, asi que escribirla vacia solo
        # dejaba en el pyproject una seccion muda que invita a preguntar que es.
        # Se emite unicamente donde tiene sentido, y con un comentario que lo
        # explique en el propio fichero.
        if chip.startswith("pic"):
            config_table = tomlkit.table()
            config_table.comment("Chip configuration bits, e.g. FOSC = \"XT\"")
            pymcu_tool.add("config", config_table)

        pymcu_toolchain = tomlkit.table()
        pymcu_toolchain.add("name", toolchain_name)
        pymcu_tool.add("toolchain", pymcu_toolchain)

        # [tool.pymcu.flash] is what `pymcu flash` reads (programmer/port/baud).
        pymcu_flash = tomlkit.table()
        pymcu_flash.add("programmer", programmer_name)
        pymcu_tool.add("flash", pymcu_flash)

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
        if not no_git and _confirm("Initialize git repository?", default=False):
            try:
                subprocess.run(
                    ["git", "init"], cwd=project_path, check=True, capture_output=True
                )
                git_inited = True
            except FileNotFoundError:
                # A minimal Ubuntu server has no git. This used to escape to the
                # handler at the bottom, which aborted `pymcu new` with a bare
                # "[Errno 2] No such file or directory: 'git'" -- after the whole
                # project had already been written. The scaffold does not need
                # git, so say what happened and carry on.
                console.print(
                    "[yellow]git is not installed — skipping repository setup.[/yellow]\n"
                    "  The project is complete without it. To add one later:\n"
                    "  [bold]git init && pymcu sync[/bold]"
                )
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
        install_deps = _confirm(
            f"Install dependencies with {pkg_manager} now?",
            default=True, non_interactive=False,
        )
        if install_deps:
            with console.status(
                f"[bold green]Installing dependencies via {pkg_manager}..."
            ):
                if pkg_manager == "uv":
                    subprocess.run(
                        [uv_bin or "uv", "sync"], cwd=project_path, check=True
                    )
                elif pkg_manager == "poetry":
                    subprocess.run(
                        ["poetry", "install"], cwd=project_path, check=True
                    )
                elif pkg_manager == "pip":
                    # Creating the venv can fail for reasons that have nothing to
                    # do with us -- a base Python built without `ensurepip` is the
                    # usual one, and Apple's Command Line Tools python3 and
                    # Debian's split python3-venv are both like that. The result
                    # used to be ignored, so the next line ran an interpreter that
                    # had never been created and the whole command died with
                    # `[Errno 2] No such file or directory: '.venv/bin/python'`,
                    # blaming a missing file instead of the real cause.
                    venv = subprocess.run(
                        [sys.executable, "-m", "venv", ".venv"],
                        cwd=project_path,
                        capture_output=True,
                        text=True,
                    )
                    # The venv layout differs by platform: Scripts/python.exe on
                    # Windows, bin/python elsewhere. Invoke pip via "python -m pip" so
                    # we don't depend on the exact pip executable name either.
                    #
                    # ABSOLUTA, y no es un detalle: `project_path` es relativa
                    # (Path(name)), asi que esto valia "blink/.venv/bin/python", y
                    # se pasaba junto con cwd=project_path. El hijo resuelve el
                    # programa contra SU cwd, o sea buscaba blink/blink/... y
                    # moria con [Errno 2] aunque el entorno estuviera creado.
                    if sys.platform == "win32":
                        venv_python = (project_path / ".venv" / "Scripts" / "python.exe").resolve()
                    else:
                        venv_python = (project_path / ".venv" / "bin" / "python").resolve()

                    if venv.returncode != 0 or not venv_python.is_file():
                        detail = (venv.stderr or venv.stdout or "").strip()
                        console.print(
                            "[yellow]Could not create the project's virtual environment, "
                            "so dependencies were not installed.[/yellow]"
                        )
                        if detail:
                            console.print(f"[dim]{detail.splitlines()[-1]}[/dim]")
                        console.print(
                            "[dim]The project itself is fine. Create the environment "
                            "yourself and install into it, then `pymcu build`.[/dim]"
                        )
                        install_deps = False
                    else:
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

        # Without its dependencies the project does not compile -- `pymcu build`
        # fails on the compat import -- so never leave that state unexplained.
        if not install_deps:
            if pkg_manager == "uv":
                install_cmd = "uv sync"
            elif pkg_manager == "poetry":
                install_cmd = "poetry install"
            else:
                install_cmd = (
                    "python -m venv .venv && .venv\\Scripts\\pip install -r requirements.txt"
                    if sys.platform == "win32"
                    else "python -m venv .venv && .venv/bin/pip install -r requirements.txt"
                )
            console.print(
                f"\n[yellow]Dependencies are not installed yet.[/yellow] "
                f"Run this before [bold]pymcu build[/bold]:\n"
                f"  [bold]cd {name} && {install_cmd}[/bold]"
            )

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
