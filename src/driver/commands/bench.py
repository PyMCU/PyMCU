# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------

"""pymcu bench — compile + simulate + report per-function cycle statistics."""

from __future__ import annotations

import importlib.util
import json
import shutil
import subprocess
import sys
import tempfile
from collections import defaultdict
from pathlib import Path
from typing import Optional

import tomlkit
import typer
from rich.console import Console
from rich.table import Table

from ..backends import get_backend_for_chip, run_backend
from ..core.boards import BOARD_CHIPS
from ..core.compiler import PyMCUCompiler
from .profile import _get_profiler_binary

console = Console()


def _compute_stats(profile_data: dict) -> tuple[list[dict], int]:
    """Compute per-function stats from a Speedscope evented profile.

    Returns (stats_list, total_simulation_cycles).
    Each entry in stats_list has:
      name, calls, self_cycles, total_incl, avg_incl, min_incl, max_incl
    Sorted by self_cycles descending.
    """
    frames = profile_data["shared"]["frames"]
    events = profile_data["profiles"][0]["events"]
    end_value = int(profile_data["profiles"][0]["endValue"])

    # Stack-based pass: compute inclusive durations and self-time simultaneously.
    # Self-time = cycles where a frame is the innermost frame on the call stack.
    stack: list[tuple[int, int]] = []   # (frame_idx, open_at)
    inclusive_durations: dict[int, list[int]] = defaultdict(list)
    self_cycles: dict[int, int] = defaultdict(int)
    prev_at: int = 0

    for event in events:
        at = int(event["at"])
        frame_idx = int(event["frame"])

        if event["type"] == "O":
            if stack:
                self_cycles[stack[-1][0]] += at - prev_at
            stack.append((frame_idx, at))
            prev_at = at

        elif event["type"] == "C":
            if stack:
                top_idx, open_at = stack.pop()
                self_cycles[top_idx] += at - prev_at
                inclusive_durations[top_idx].append(at - open_at)
                prev_at = at

    results: list[dict] = []
    for idx, frame in enumerate(frames):
        durs = inclusive_durations.get(idx)
        if not durs:
            continue
        total_incl = sum(durs)
        self_t = self_cycles.get(idx, 0)
        results.append({
            "name": frame["name"],
            "calls": len(durs),
            "self_cycles": self_t,
            "total_incl": total_incl,
            "avg_incl": total_incl // len(durs),
            "min_incl": min(durs),
            "max_incl": max(durs),
        })

    results.sort(key=lambda x: x["self_cycles"], reverse=True)
    return results, end_value


def _fmt_cycles(n: int) -> str:
    if n >= 1_000_000:
        return f"{n / 1_000_000:.2f}M"
    if n >= 1_000:
        return f"{n / 1_000:.1f}k"
    return str(n)


def bench(
    cycles: Optional[int] = typer.Option(None, "--cycles", help="Cycles to simulate"),
    ms: Optional[float] = typer.Option(None, "--ms", help="Simulated milliseconds (default: 100)"),
    freq_override: Optional[int] = typer.Option(None, "--freq", help="Override clock frequency (Hz)"),
    top: int = typer.Option(0, "--top", help="Show only top N functions (0 = all)"),
    verbose: bool = typer.Option(False, "-v", "--verbose"),
):
    """Compile + simulate + report per-function cycle statistics."""

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

    extra_includes: list[str] = []
    for flavor in pymcu_cfg.get("stdlib", []):
        spec = importlib.util.find_spec(f"pymcu_{flavor}")
        if spec and spec.submodule_search_locations:
            pkg_dir = Path(list(spec.submodule_search_locations)[0])
            extra_includes.append(str(pkg_dir.parent))
            extra_includes.append(str(pkg_dir))

    console.print(f"[cyan]Benchmarking[/cyan] {entry_point} → {chip} @ {freq:,} Hz")

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

    # ── 3. Assemble to HEX ───────────────────────────────────────────────────
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

    # ── 4. Run profiler to temp file ─────────────────────────────────────────
    profiler_bin = _get_profiler_binary()
    if not profiler_bin.exists():
        console.print("[red]pymcuc-avr-profiler not found.[/red]")
        console.print("  Build it: dotnet publish extensions/pymcu-avr/src/csharp/profiler/ -o build/bin/")
        raise typer.Exit(1)

    with tempfile.NamedTemporaryFile(suffix=".speedscope.json", delete=False) as tf:
        tmp_path = tf.name

    cmd = [
        str(profiler_bin),
        str(hex_path),
        "--symbols", str(symbols_path),
        "-o", tmp_path,
        "--freq", str(freq),
        "--name", f"firmware ({chip} @ {freq // 1_000_000}MHz)",
    ]
    if cycles is not None:
        cmd += ["--cycles", str(cycles)]
    elif ms is not None:
        cmd += ["--ms", str(ms)]
    else:
        cmd += ["--ms", "100"]

    console.print("[cyan]Simulating...[/cyan]")
    result = subprocess.run(cmd, text=True, capture_output=not verbose)
    if result.returncode != 0:
        console.print("[red]Profiler failed:[/red]")
        console.print(result.stderr or result.stdout)
        raise typer.Exit(1)

    # ── 5. Parse stats and display ────────────────────────────────────────────
    with open(tmp_path) as f:
        profile_data = json.load(f)
    Path(tmp_path).unlink(missing_ok=True)

    stats, total_cycles = _compute_stats(profile_data)
    total_ms = total_cycles / freq * 1000

    if top > 0:
        stats = stats[:top]

    table = Table(
        title=f"Simulated {total_ms:.1f} ms  ({total_cycles:,} cycles @ {freq // 1_000_000} MHz)",
        show_lines=False,
        expand=False,
    )
    table.add_column("Function", style="bold", no_wrap=True)
    table.add_column("Calls", justify="right")
    table.add_column("Self", justify="right")
    table.add_column("Self %", justify="right")
    table.add_column("Avg/call", justify="right")
    table.add_column("Incl %", justify="right", style="dim")

    for row in stats:
        self_pct = row["self_cycles"] / total_cycles * 100 if total_cycles else 0
        incl_pct = row["total_incl"] / total_cycles * 100 if total_cycles else 0

        if self_pct >= 30:
            pct_style = "[red]"
        elif self_pct >= 10:
            pct_style = "[yellow]"
        else:
            pct_style = ""

        table.add_row(
            row["name"],
            f"{row['calls']:,}",
            _fmt_cycles(row["self_cycles"]),
            f"{pct_style}{self_pct:.1f}%",
            _fmt_cycles(row["avg_incl"]),
            f"{incl_pct:.1f}%",
        )

    console.print(table)
