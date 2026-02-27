# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# This file is part of the PyMCU Development Ecosystem.
#
# PyMCU is free software: you can redistribute it and/or modify
# it under the terms of the GNU General Public License as published by
# the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# PyMCU is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
# GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with PyMCU.  If not, see <https://www.gnu.org/licenses/>.
#
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
from rich.console import Console
from rich.progress import Progress, SpinnerColumn, TextColumn, BarColumn, TimeElapsedColumn

# New Architecture Imports
from ..toolchains import get_toolchain_for_chip
from ..core.compiler import PyMcuCompiler

console = Console()


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

        pymcu_config = config.get("tool", {}).get("pymcu", {})
        chip = pymcu_config.get("chip", "pic16f84a")
        freq = pymcu_config.get("frequency", 4000000)
        src_path = pymcu_config.get("sources", "src")

        project_root = pyproject_path.parent.absolute()
        sources_dir = (project_root / src_path).resolve()

        entry_file_name = pymcu_config.get("entry", "main.py")
        entry_point = (sources_dir / entry_file_name).resolve()

        output_dir = project_root / "dist"
        output_file = output_dir / "firmware.asm"

        if not entry_point.exists():
            console.print(f"[red]Entry point not found at: {entry_point}[/red]")
            console.print(f"[yellow]Check 'sources' and 'entry' in pyproject.toml (current: sources={src_path}, entry={entry_file_name})[/yellow]")
            raise typer.Exit(code=1)
        
        config_map = {}
        tool_config = pymcu_config.get("config", {})
        for key, val in tool_config.items():
            config_map[str(key)] = str(val)

        # Read vector configuration for bootloader support
        vectors_config = pymcu_config.get("vectors", {})
        reset_vector = vectors_config.get("reset", None)
        interrupt_vector = vectors_config.get("interrupt", None)

        if not Path(entry_point).exists():
            console.print(f"[red]Entry point not found at: {entry_point}[/red]")
            console.print(f"[yellow]Check 'sources' in pyproject.toml (current: {src_path})[/yellow]")
            raise typer.Exit(code=1)

        if not output_dir.exists():
            output_dir.mkdir(parents=True)

        # 1. Factory: Get the appropriate toolchain strategy
        # Note: We rely on the chip name to decide. Assembler override in TOML could be handled
        # by passing it to the factory if needed, but for now we follow the simple Architecture.
        toolchain = get_toolchain_for_chip(chip, console)
        
        # 2. Interactive Install Check (BEFORE Progress Bar)
        if not toolchain.is_cached():
            try:
                toolchain.install()
            except RuntimeError as e:
                console.print(f"[bold red]Toolchain installation failed:[/bold red] {e}")
                raise typer.Exit(code=1)

        # 3. Core Compiler Wrapper
        compiler = PyMcuCompiler(console)

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
                    interrupt_vector=interrupt_vector)
            except RuntimeError as e:
                progress.stop()
                console.print(f"[bold red]Compilation Error:[/bold red] {e}")
                raise typer.Exit(code=1)
                
            progress.update(build_task, completed=50)
            
            # Step 1.5: Library Injection (Float Support & AVR Math)
            with open(output_file, "r") as asm_f:
                asm_content = asm_f.read()
            
            import importlib.util
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
                if toolchain.get_name() == "avra":
                    progress.update(build_task, description="Injecting AVR Math Runtime...")
                    avr_math_path = math_lib_path / "avr"
                    
                    # List of runtime functions to check
                    runtime_funcs = ["__div8", "__mod8", "__mul8", "__div16"]
                    needed_funcs = [f for f in runtime_funcs if f in asm_content]
                    
                    if needed_funcs:
                        with open(output_file, "a") as asm_f:
                            asm_f.write("\n; --- PyMCU AVR Math Runtime ---\n")
                            
                            # Map function names to source files
                            func_map = {
                                "__div8": "div.S",
                                "__mod8": "div.S",
                                "__mul8": "mul.S",
                                "__div16": "div16.S"
                            }
                            
                            included_files = set()
                            for func in needed_funcs:
                                fname = func_map.get(func)
                                if fname and fname not in included_files:
                                    src_path = avr_math_path / fname
                                    if src_path.exists():
                                        with open(src_path, "r") as lib_f:
                                            asm_f.write(lib_f.read())
                                            asm_f.write("\n")
                                        included_files.add(fname)
                                    else:
                                        console.print(f"[bold yellow]Warning:[/bold yellow] Runtime file {fname} not found")

            else:
                console.print("[bold yellow]Warning:[/bold yellow] pymcu-stdlib not installed, math operations may fail.")

            # Step 2: Assembly (ASM -> HEX)
            progress.update(build_task, description="Assembling...", completed=60)
            try:
                # The toolchain strategy handles the specific assembler invocation (gpasm, etc.)
                hex_file = toolchain.assemble(output_file)
            except RuntimeError as e:
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
                        "atmega2560": 262144, "atmega168": 16384,
                        "atmega88": 8192, "atmega48": 4096,
                        "attiny85": 8192, "attiny84": 8192,
                        "attiny2313": 2048,
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
