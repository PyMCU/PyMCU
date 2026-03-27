# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
#
# Permission is hereby granted, free of charge, to any person obtaining a copy
# of this software and associated documentation files (the "Software"), to deal
# in the Software without restriction, including without limitation the rights
# to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
# copies of the Software, and to permit persons to whom the Software is
# furnished to do so, subject to the following conditions:
#
# The above copyright notice and this permission notice shall be included in
# all copies or substantial portions of the Software.
#
# THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
# IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
# FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
# AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
# LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
# OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
# THE SOFTWARE.
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

from pathlib import Path
import tomlkit
import typer
import os
import shutil
import importlib.util
from rich.console import Console
from rich.progress import Progress, SpinnerColumn, TextColumn, BarColumn, TimeElapsedColumn

# New Architecture Imports
from ..toolchains import get_toolchain_for_chip, get_ffi_toolchain_for_chip
from ..core.compiler import WhipCompiler

console = Console()

# ---------------------------------------------------------------------------
# Board → chip mapping
# ---------------------------------------------------------------------------
# When pyproject.toml uses  board = "arduino_uno"  instead of  chip = "...",
# the chip is derived from this dict.  Extension packages may supplement it
# via a board_chips.py module (see _load_extension_board_chips()).
# ---------------------------------------------------------------------------
BOARD_CHIPS: dict[str, str] = {
    "arduino_uno":   "atmega328p",
    "arduino_nano":  "atmega328p",
    "arduino_mega":  "atmega2560",
    "arduino_micro": "atmega32u4",
}


def _load_extension_board_chips(flavor: str) -> dict[str, str]:
    """
    Try to import pymcu_<flavor>.board_chips and return its BOARD_CHIPS dict.
    Returns an empty dict if the module or attribute does not exist.
    """
    try:
        mod = importlib.import_module(f"pymcu_{flavor}.board_chips")
        return dict(getattr(mod, "BOARD_CHIPS", {}))
    except Exception:
        return {}


def _resolve_chip_for_board(board: str, extra: dict[str, str]) -> str | None:
    """Return the chip name for *board*, checking extension-supplied entries first."""
    return extra.get(board) or BOARD_CHIPS.get(board)


def _parse_hex_flash_bytes(hex_file: Path) -> int:
    """
    Parse an Intel HEX file and return the total number of data bytes.
    Only counts type-00 (data) records; ignores EOF (01) and extended (02/04) records.
    Returns 0 if the file cannot be read.
    """
    total = 0
    try:
        with open(hex_file, "r") as f:
            for line in f:
                line = line.strip()
                if not line.startswith(":"):
                    continue
                rec_len  = int(line[1:3], 16)
                rec_type = int(line[7:9], 16)
                if rec_type == 0x00:   # data record
                    total += rec_len
    except Exception:
        pass
    return total

def build(verbose: bool = typer.Option(False, "--verbose", "-v", help="Enable verbose logging")):
    pyproject_path = Path("pyproject.toml")
    if not pyproject_path.exists():
        console.print("[red]No pyproject.toml found. Are you in a pymcu project?[/red]")
        raise typer.Exit(code=1)

    try:
        with open(pyproject_path, "r") as f:
            config = tomlkit.load(f)

        whip_config = config.get("tool", {}).get("pymcu", {})
        chip_key  = whip_config.get("chip",  None)
        board_key = whip_config.get("board", None)
        freq      = whip_config.get("frequency", 4000000)
        src_path  = whip_config.get("sources", "src")

        # board and chip are mutually exclusive
        if chip_key and board_key:
            implied = BOARD_CHIPS.get(board_key, "?")
            console.print(
                f"[bold red]Error:[/bold red] Cannot set both 'chip' and 'board' in \\[tool.pymcu].\n"
                f"  'board = \"{board_key}\"' implies chip = \"{implied}\". Remove the 'chip' key."
            )
            raise typer.Exit(code=1)

        # Resolve extension packages and their extra board→chip entries
        stdlib_flavors = whip_config.get("stdlib", [])
        extension_board_chips: dict[str, str] = {}
        extra_includes: list[str] = []
        extension_board_dirs: dict[str, Path] = {}  # flavor -> boards/ dir

        for flavor in stdlib_flavors:
            spec = importlib.util.find_spec(f"pymcu_{flavor}")
            if spec and spec.submodule_search_locations:
                pkg_dir = Path(list(spec.submodule_search_locations)[0])
                pkg_parent = pkg_dir.parent
                extra_includes.append(str(pkg_parent))
                extra_includes.append(str(pkg_dir))
                # Collect board_chips supplements
                extension_board_chips.update(_load_extension_board_chips(flavor))
                # Record boards/ dir for shim generation
                boards_dir = pkg_dir / "boards"
                if boards_dir.is_dir():
                    extension_board_dirs[flavor] = boards_dir
            else:
                console.print(
                    f"[bold yellow]Warning:[/bold yellow] stdlib flavor 'pymcu_{flavor}' not found. "
                    f"Install it with: pip install pymcu-{flavor}"
                )

        # Derive chip from board or fall back to explicit chip / default
        if board_key:
            chip = _resolve_chip_for_board(board_key, extension_board_chips)
            if chip is None:
                console.print(
                    f"[bold red]Error:[/bold red] Unknown board '{board_key}'. "
                    f"Add it to BOARD_CHIPS in build.py or provide a board_chips.py in your extension package."
                )
                raise typer.Exit(code=1)
        else:
            chip = chip_key or "pic16f84a"

        project_root = pyproject_path.parent.absolute()
        sources_dir = (project_root / src_path).resolve()

        entry_file_name = whip_config.get("entry", "main.py")
        entry_point = (sources_dir / entry_file_name).resolve()

        output_dir = project_root / "dist"
        output_file = output_dir / "firmware.asm"

        if not entry_point.exists():
            console.print(f"[red]Entry point not found at: {entry_point}[/red]")
            console.print(f"[yellow]Check 'sources' and 'entry' in pyproject.toml (current: sources={src_path}, entry={entry_file_name})[/yellow]")
            raise typer.Exit(code=1)
        
        config_map = {}
        tool_config = whip_config.get("config", {})
        for key, val in tool_config.items():
            config_map[str(key)] = str(val)

        # Read vector configuration for bootloader support
        vectors_config = whip_config.get("vectors", {})
        reset_vector = vectors_config.get("reset", None)
        interrupt_vector = vectors_config.get("interrupt", None)

        if not Path(entry_point).exists():
            console.print(f"[red]Entry point not found at: {entry_point}[/red]")
            console.print(f"[yellow]Check 'sources' in pyproject.toml (current: {src_path})[/yellow]")
            raise typer.Exit(code=1)

        if not output_dir.exists():
            output_dir.mkdir(parents=True)

        # Generate dist/_generated/board.py shim when board= is set.
        # This shim is prepended to -I so `import board` finds it first.
        if board_key:
            generated_dir = output_dir / "_generated"
            generated_dir.mkdir(parents=True, exist_ok=True)
            board_shim = generated_dir / "board.py"

            # Find which extension (if any) has boards/<board>.py.
            # We copy the board file content directly into board.py so that
            # `import board` works without star-import (not supported by pymcuc).
            src_board_file = None
            for flavor, boards_dir in extension_board_dirs.items():
                candidate = boards_dir / f"{board_key}.py"
                if candidate.exists():
                    src_board_file = candidate
                    break

            if src_board_file:
                board_shim_content = (
                    f"# Auto-generated by pymcu build -- do not edit\n"
                    + src_board_file.read_text()
                )
            else:
                # Vanilla fallback: copy the vanilla board file directly
                try:
                    import pymcu as _pymcu_pkg
                    vanilla_board = Path(__pymcu_pkg.__file__).parent / "boards" / f"{board_key}.py"
                    if vanilla_board.exists():
                        board_shim_content = (
                            "# Auto-generated by pymcu build -- do not edit\n"
                            + vanilla_board.read_text()
                        )
                    else:
                        raise FileNotFoundError
                except Exception:
                    console.print(f"[bold yellow]Warning:[/bold yellow] No board file found for '{board_key}'.")
                    board_shim_content = f"# Auto-generated by pymcu build -- no board file found for {board_key}\n"

            board_shim.write_text(board_shim_content)
            # Prepend generated dir so `import board` finds the shim first
            extra_includes.insert(0, str(generated_dir))

        # Detect C interop: [tool.pymcu.ffi] sources = [...]
        ffi_config = whip_config.get("ffi", {})
        ffi_sources_raw: list[str] = list(ffi_config.get("sources", []))
        use_ffi = bool(ffi_sources_raw)

        # 1. Factory: Get the appropriate toolchain strategy.
        # When [tool.pymcu.ffi] sources are declared the GNU binutils pipeline
        # (avr-as + avr-ld + avr-objcopy) is used instead of avra.
        if use_ffi:
            try:
                toolchain = get_ffi_toolchain_for_chip(chip, console)
            except ValueError as e:
                console.print(f"[bold red]Error:[/bold red] {e}")
                raise typer.Exit(code=1)
        else:
            toolchain = get_toolchain_for_chip(chip, console)

        # 2. Interactive Install Check (BEFORE Progress Bar)
        if not toolchain.is_cached():
            try:
                toolchain.install()
            except RuntimeError as e:
                console.print(f"[bold red]Toolchain installation failed:[/bold red] {e}")
                raise typer.Exit(code=1)

        # 3. Core Compiler Wrapper
        compiler = WhipCompiler(console)

        with Progress(
            SpinnerColumn(),
            TextColumn("[progress.description]{task.description}"),
            BarColumn(),
            TimeElapsedColumn(),
            transient=False,
            console=console
        ) as progress:
            
            build_task = progress.add_task(description=f"Building {chip}...", total=100)
            
            # Step 1: Compilation (Python -> ASM)
            progress.update(build_task, description="Compiling Python to ASM...", completed=10)
            try:
                compiler.compile(
                    input_file=entry_point,
                    output_file=str(output_file),
                    arch=chip,
                    freq=freq,
                    configs=config_map,
                    search_path=sources_dir,
                    verbose=verbose,
                    reset_vector=reset_vector,
                    interrupt_vector=interrupt_vector,
                    extra_includes=extra_includes or None)
            except RuntimeError as e:
                progress.stop()
                console.print(f"[bold red]Compilation Error:[/bold red] {e}")
                raise typer.Exit(code=1)
                
            progress.update(build_task, completed=50)
            
            # Step 1.5: Library Injection (Float Support & AVR Math)
            with open(output_file, "r") as asm_f:
                asm_content = asm_f.read()
            
            spec = importlib.util.find_spec("pymcu.math")
            
            if spec and spec.origin:
                math_lib_path = Path(spec.origin).parent
                
                # PIC Float Support
                if '#include "float.inc"' in asm_content:
                    progress.update(build_task, description="Injecting Float Library...")
                    arch = "pic16" # Default for PIC10/12/16
                    if chip.lower().startswith("pic18"):
                        arch = "pic18"
                        
                    src_inc = math_lib_path / arch / "float.inc"
                    dst_inc = output_dir / "float.inc"
                    
                    if src_inc.exists():
                        shutil.copy(str(src_inc), str(dst_inc))
                    else:
                        console.print(f"[bold yellow]Warning:[/bold yellow] float.inc not found for {arch}")

                # AVR Math Runtime Injection
                # If we are targeting AVR, we need to assemble and link the math runtime
                # Since AVRA doesn't support linking multiple objects easily like ld,
                # we will append the math assembly source directly to the output file
                # if the compiler emitted calls to __div8, __mod8, etc.
                if toolchain.get_name() in ("avra", "avr-as"):
                    progress.update(build_task, description="Injecting AVR Math Runtime...")
                    avr_math_path = math_lib_path / "avr"
                    
                    # List of runtime functions to check
                    runtime_funcs = ["__div8", "__mod8", "__mul8", "__div16"]
                    needed_funcs = [f for f in runtime_funcs if f in asm_content]
                    
                    if needed_funcs:
                        # Build the math runtime text
                        func_map = {
                            "__div8": "div.S",
                            "__mod8": "div.S",
                            "__mul8": "mul.S",
                            "__div16": "div16.S"
                        }
                        math_runtime_text = "\n; --- Whipsnake AVR Math Runtime ---\n"
                        included_files = set()
                        for func in needed_funcs:
                            fname = func_map.get(func)
                            if fname and fname not in included_files:
                                src_path = avr_math_path / fname
                                if src_path.exists():
                                    with open(src_path, "r") as lib_f:
                                        math_runtime_text += lib_f.read() + "\n"
                                    included_files.add(fname)
                                else:
                                    console.print(f"[bold yellow]Warning:[/bold yellow] Runtime file {fname} not found")

                        # Insert math runtime BEFORE the first function label so that
                        # __div8/__mod8 are at a low word address, within RCALL range
                        # (±2047 words) of any call site in large firmware images.
                        with open(output_file, "r") as f:
                            lines = f.readlines()

                        insert_idx = len(lines)  # fallback: append
                        past_vector_table = False
                        for i, line in enumerate(lines):
                            stripped = line.strip()
                            if stripped.startswith(".org"):
                                past_vector_table = True
                            elif past_vector_table and stripped and not stripped.startswith(";") \
                                    and not stripped.startswith(".") \
                                    and stripped.endswith(":"):
                                # First function label after the vector table
                                insert_idx = i
                                break

                        lines.insert(insert_idx, math_runtime_text + "\n")
                        with open(output_file, "w") as f:
                            f.writelines(lines)

            else:
                console.print("[bold yellow]Warning:[/bold yellow] pymcu-stdlib not installed, math operations may fail.")

            # Step 2: Assembly (ASM -> HEX)
            progress.update(build_task, description="Assembling...", completed=60)
            hex_file: Path | None = None
            try:
                if use_ffi:
                    # ── FFI pipeline: avr-as + avr-gcc + avr-ld + avr-objcopy ──────────
                    from ..toolchains.avrgas import AvrgasToolchain as _AvrgasToolchain
                    ffi_tc: _AvrgasToolchain = toolchain  # type: ignore[assignment]

                    # 2a. Assemble firmware.asm → firmware.o (ELF)
                    firmware_obj = ffi_tc.assemble(output_file)

                    # 2b. Compile C sources declared in [tool.pymcu.ffi]
                    progress.update(build_task, description="Compiling C sources...", completed=65)
                    c_source_paths = [
                        (project_root / p).resolve() for p in ffi_sources_raw
                    ]
                    include_dirs_raw: list[str] = list(ffi_config.get("include_dirs", []))
                    include_dirs = [
                        (project_root / d).resolve() for d in include_dirs_raw
                    ]
                    cflags: list[str] = list(ffi_config.get("cflags", []))
                    c_objects = ffi_tc.compile_c(
                        c_source_paths, include_dirs, cflags, output_dir
                    )

                    # 2c. Link firmware.o + C objects → firmware.elf
                    progress.update(build_task, description="Linking...", completed=75)
                    linker_script_rel: str | None = ffi_config.get("linker_script", None)
                    linker_script_path = (
                        (project_root / linker_script_rel).resolve()
                        if linker_script_rel else None
                    )
                    elf_file = ffi_tc.link(
                        firmware_obj, c_objects, output_dir, linker_script_path
                    )

                    # 2d. ELF → Intel HEX
                    progress.update(build_task, description="Generating HEX...", completed=85)
                    hex_file = ffi_tc.elf_to_hex(elf_file)

                    # Move ELF to dist/debug/
                    debug_dir = output_dir / "debug"
                    debug_dir.mkdir(parents=True, exist_ok=True)
                    shutil.move(str(elf_file), str(debug_dir / elf_file.name))
                    # Clean up intermediate objects
                    for obj in [firmware_obj] + c_objects:
                        if obj.exists():
                            obj.unlink()

                elif toolchain.get_name() == "avr-as":
                    # ── avr-as pipeline (non-FFI): assemble → link → objcopy ───────────
                    # Same as FFI but without C compilation.
                    from ..toolchains.avrgas import AvrgasToolchain as _AvrgasToolchain
                    gas_tc: _AvrgasToolchain = toolchain  # type: ignore[assignment]

                    firmware_obj = gas_tc.assemble(output_file)
                    progress.update(build_task, description="Linking...", completed=75)
                    elf_file = gas_tc.link(firmware_obj, [], output_dir)
                    progress.update(build_task, description="Generating HEX...", completed=85)
                    hex_file = gas_tc.elf_to_hex(elf_file)

                    debug_dir = output_dir / "debug"
                    debug_dir.mkdir(parents=True, exist_ok=True)
                    shutil.move(str(elf_file), str(debug_dir / elf_file.name))
                    if firmware_obj.exists():
                        firmware_obj.unlink()

                else:
                    # ── Standard avra pipeline with linker-relaxation ─────────────────
                    import re as _re
                    last_exc = None
                    # Linker-relaxation loop: start with RJMP/RCALL (1 word, ±2047 range).
                    # If AVRA reports out-of-range errors at specific lines, upgrade only
                    # those instructions to JMP/CALL (2 words, full 22-bit range) and retry.
                    for _pass in range(8):
                        try:
                            hex_file = toolchain.assemble(output_file)
                            break
                        except RuntimeError as e:
                            err_str = str(e)
                            if "out of range" not in err_str.lower() or toolchain.get_name() != "avra":
                                last_exc = e
                                break
                            bad_lines = set(
                                int(m) - 1
                                for m in _re.findall(r"\((\d+)\)", err_str)
                            )
                            if not bad_lines:
                                last_exc = e
                                break
                            with open(output_file, "r") as f:
                                asm_lines = f.readlines()
                            for idx in bad_lines:
                                if 0 <= idx < len(asm_lines):
                                    ln = asm_lines[idx]
                                    ln = ln.replace("\tRCALL\t", "\tCALL\t")
                                    ln = ln.replace("\tRJMP\t",  "\tJMP\t")
                                    asm_lines[idx] = ln
                            with open(output_file, "w") as f:
                                f.writelines(asm_lines)
                            last_exc = e
                    if hex_file is None:
                        progress.stop()
                        console.print(f"[bold red]Assembly Error:[/bold red] {last_exc}")
                        raise typer.Exit(code=1)

            except typer.Exit:
                raise
            except Exception as e:
                progress.stop()
                console.print(f"[bold red]Assembly Error:[/bold red] {e}")
                raise typer.Exit(code=1)

            progress.update(build_task, completed=90)

            # Step 2.5: Flash size report (HEX parse) + optional ELF generation
            if toolchain.get_name() == "avra":
                progress.update(build_task, description="Reporting size...")
                flash_bytes = _parse_hex_flash_bytes(hex_file)
                if flash_bytes > 0:
                    # Chip flash sizes (bytes) — extend as needed
                    flash_sizes = {
                        "atmega328p": 32768, "atmega328": 32768,
                        "atmega168p": 16384, "atmega168": 16384,
                        "atmega88p": 8192,   "atmega88": 8192,
                        "atmega48p": 4096,   "atmega48": 4096,
                        "atmega2560": 262144,
                        "attiny85": 8192,  "attiny45": 4096,  "attiny25": 2048,
                        "attiny84": 8192,  "attiny44": 4096,  "attiny24": 2048,
                        "attiny13": 1024,  "attiny13a": 1024,
                        "attiny2313": 2048, "attiny4313": 4096,
                    }
                    flash_total = flash_sizes.get(chip.lower(), 0)
                    if flash_total:
                        pct = flash_bytes * 100 // flash_total
                        console.print(
                            f"[dim]Flash:[/dim] {flash_bytes} / {flash_total} bytes "
                            f"({pct}% of program storage)"
                        )
                    else:
                        console.print(f"[dim]Flash:[/dim] {flash_bytes} bytes")

                # Optional ELF conversion for debug tooling (avr-objcopy)
                link_result = toolchain.link(hex_file, chip, output_dir)
                if link_result:
                    elf_path, _ = link_result
                    debug_dir = output_dir / "debug"
                    if not debug_dir.exists():
                        debug_dir.mkdir(parents=True)
                    shutil.move(str(elf_path), str(debug_dir / elf_path.name))
            elif hex_file is not None:
                # Flash size report for avr-as builds (FFI or non-FFI).
                # ELF is already moved to debug/ by the assembly step above.
                progress.update(build_task, description="Reporting size...")
                flash_bytes = _parse_hex_flash_bytes(hex_file)
                if flash_bytes > 0:
                    flash_sizes = {
                        "atmega328p": 32768, "atmega328": 32768,
                        "atmega2560": 262144, "atmega168": 16384,
                        "atmega88": 8192, "atmega48": 4096,
                        "attiny85": 8192,  "attiny45": 4096,  "attiny25": 2048,
                        "attiny84": 8192,  "attiny44": 4096,  "attiny24": 2048,
                        "attiny13": 1024,  "attiny13a": 1024,
                        "attiny2313": 2048, "attiny4313": 4096,
                    }
                    flash_total = flash_sizes.get(chip.lower(), 0)
                    if flash_total:
                        pct = flash_bytes * 100 // flash_total
                        console.print(
                            f"[dim]Flash:[/dim] {flash_bytes} / {flash_total} bytes "
                            f"({pct}% of program storage)"
                        )
                    else:
                        console.print(f"[dim]Flash:[/dim] {flash_bytes} bytes")

            # Step 3: Cleanup
            progress.update(build_task, description="Cleaning up...")
            
            # Move extra files to dist/debug
            debug_dir = output_dir / "debug"
            for ext in [".lst", ".cod", ".map", ".asm", ".obj", ".cof"]: # Added .obj, .cof for AVRA
                f = output_file.with_suffix(ext)
                if f.exists():
                    if not debug_dir.exists():
                        debug_dir.mkdir(parents=True)
                    shutil.move(str(f), str(debug_dir / f.name))
            
            progress.update(build_task, description="Done!", completed=100)

        console.print(f"[bold green]Build successful![/bold green] Artifacts in: [blue]{output_dir}[/blue]")

    except Exception as e:
        console.print(f"[bold red]Error:[/bold red] {e}")
        raise typer.Exit(code=1)
