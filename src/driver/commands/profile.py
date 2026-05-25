# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------

"""pymcu profile — compile + simulate + generate Speedscope flamegraph."""

from __future__ import annotations

import importlib.util
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Optional

import tomlkit
import typer
from rich.console import Console

from ..backends import get_backend_for_chip, run_backend
from ..core.boards import BOARD_CHIPS
from ..core.compiler import PyMCUCompiler

console = Console()


def _get_profiler_binary() -> Path:
    """Locate pymcuc-avr-profiler using the same search order as get_backend_binary."""
    binary_name = "pymcuc-avr-profiler.exe" if sys.platform == "win32" else "pymcuc-avr-profiler"

    # 1. Adjacent to this file (wheel layout for future distribution)
    adjacent = Path(__file__).parent / binary_name
    if adjacent.exists():
        return adjacent

    # 2. build/bin/ — dotnet publish output (primary dev path)
    repo_root = Path(__file__).parents[3]
    dev_path = repo_root / "build" / "bin" / binary_name
    if dev_path.exists():
        return dev_path

    # 3. profiler Debug build output (fast iteration)
    profiler_debug = (
        Path(__file__).parents[4]
        / "extensions" / "pymcu-avr" / "src" / "csharp" / "profiler"
        / "bin" / "Debug" / "net10.0" / binary_name
    )
    if profiler_debug.exists():
        return profiler_debug

    # 4. System PATH
    which_result = shutil.which(binary_name)
    if which_result:
        return Path(which_result)

    return dev_path  # caller will get FileNotFoundError


def profile(
    cycles: Optional[int] = typer.Option(None, "--cycles", help="Cycles to simulate"),
    ms: Optional[float] = typer.Option(None, "--ms", help="Simulated milliseconds (default: 100)"),
    output: str = typer.Option("profile.speedscope.json", "-o", help="Output Speedscope JSON path"),
    open_browser: bool = typer.Option(False, "--open", help="Open speedscope.app after profiling"),
    freq_override: Optional[int] = typer.Option(None, "--freq", help="Override clock frequency (Hz)"),
    verbose: bool = typer.Option(False, "-v", "--verbose"),
):
    """Compile the project and generate a Speedscope flamegraph from AVR simulation."""

    # ── 1. Load pyproject.toml ────────────────────────────────────────────────
    pyproject_path = Path("pyproject.toml")
    if not pyproject_path.exists():
        console.print("[red]No pyproject.toml found. Run from your PyMCU project root.[/red]")
        raise typer.Exit(1)

    with pyproject_path.open() as f:
        cfg = tomlkit.load(f)

    pymcu_cfg = cfg.get("tool", {}).get("pymcu", {})
    chip: str = pymcu_cfg.get("chip") or pymcu_cfg.get("target", "atmega328p")
    freq: int = freq_override or int(pymcu_cfg.get("frequency", pymcu_cfg.get("freq", 16_000_000)))
    sources_dir = Path(pymcu_cfg.get("sources", pymcu_cfg.get("src", ".")))
    entry_point = sources_dir / Path(pymcu_cfg.get("entry", "main.py"))

    if chip.lower() in BOARD_CHIPS:
        chip = BOARD_CHIPS[chip.lower()]

    # Resolve stdlib compat packages (e.g. micropython)
    extra_includes: list[str] = []
    for flavor in pymcu_cfg.get("stdlib", []):
        spec = importlib.util.find_spec(f"pymcu_{flavor}")
        if spec and spec.submodule_search_locations:
            pkg_dir = Path(list(spec.submodule_search_locations)[0])
            extra_includes.append(str(pkg_dir.parent))
            extra_includes.append(str(pkg_dir))

    console.print(f"[cyan]Profiling[/cyan] {entry_point} → {chip} @ {freq:,} Hz")

    # ── 2. Build with --emit-symbols ──────────────────────────────────────────
    dist = Path("dist")
    dist.mkdir(exist_ok=True)
    symbols_path = dist / "firmware.symbols.json"
    hex_path = dist / "firmware.hex"
    asm_path = dist / "firmware.asm"
    ir_path = dist / "firmware.mir"

    compiler = PyMCUCompiler(console)
    backend_plugin = get_backend_for_chip(chip)
    if backend_plugin is None:
        console.print(f"[red]No backend found for chip '{chip}'. Is pymcu-avr installed?[/red]")
        raise typer.Exit(1)

    try:
        compiler.compile(
            input_file=entry_point,
            output_file=str(asm_path),
            target=chip,
            freq=freq,
            configs={},
            search_path=sources_dir,
            verbose=verbose,
            emit_ir_path=str(ir_path),
            extra_includes=extra_includes or None,
        )
        run_backend(
            backend_binary=backend_plugin.get_backend_binary(),
            ir_file=ir_path,
            output_file=asm_path,
            target=chip,
            freq=freq,
            configs={},
            verbose=verbose,
            emit_symbols_path=symbols_path,
        )
    except Exception as ex:
        console.print(f"[red]Build failed:[/red] {ex}")
        raise typer.Exit(1)

    # ── 3. Assemble to HEX (avr-as pipeline) ─────────────────────────────────
    from ..toolchains import get_toolchain_for_chip
    try:
        toolchain = get_toolchain_for_chip(chip, console)
    except ValueError as ex:
        console.print(f"[red]{ex}[/red]")
        raise typer.Exit(1)

    try:
        obj = toolchain.assemble(asm_path)
        elf = toolchain.link(obj, [], dist)
        result_hex = toolchain.elf_to_hex(elf)
        if result_hex.resolve() != hex_path.resolve():
            shutil.copy(result_hex, hex_path)
    except Exception as ex:
        console.print(f"[red]Assembly failed:[/red] {ex}")
        raise typer.Exit(1)

    if not hex_path.exists():
        console.print(f"[red]Assembler did not produce {hex_path}[/red]")
        raise typer.Exit(1)

    # ── 4. Run profiler ───────────────────────────────────────────────────────
    profiler_bin = _get_profiler_binary()
    if not profiler_bin.exists():
        console.print(f"[red]pymcuc-avr-profiler not found.[/red]")
        console.print("  Build it: dotnet publish extensions/pymcu-avr/src/csharp/profiler/ -o build/bin/")
        raise typer.Exit(1)

    cmd = [
        str(profiler_bin),
        str(hex_path),
        "--symbols", str(symbols_path),
        "-o", output,
        "--freq", str(freq),
        "--name", f"firmware ({chip} @ {freq // 1_000_000}MHz)",
    ]
    if cycles is not None:
        cmd += ["--cycles", str(cycles)]
    elif ms is not None:
        cmd += ["--ms", str(ms)]
    else:
        cmd += ["--ms", "100"]

    console.print(f"[cyan]Simulating...[/cyan]")
    result = subprocess.run(cmd, text=True, capture_output=not verbose)
    if result.returncode != 0:
        console.print(f"[red]Profiler failed:[/red]")
        console.print(result.stderr or result.stdout)
        raise typer.Exit(1)

    if verbose and result.stdout:
        console.print(result.stdout)

    # ── 5. Report ─────────────────────────────────────────────────────────────
    console.print(f"[green]Profile written:[/green] {output}")
    console.print("  Drag the file to [link=https://speedscope.app]https://speedscope.app[/link] to view the flamegraph.")

    if open_browser:
        import webbrowser
        webbrowser.open("https://speedscope.app")
        console.print("  [dim](Tip: drag the JSON file onto the speedscope page)[/dim]")
