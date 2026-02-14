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
        
        config_map = {}
        tool_config = pymcu_config.get("config", {})
        for key, val in tool_config.items():
            config_map[str(key)] = str(val)

        entry_point = "src/main.py"
        output_dir = Path("dist")
        output_file = output_dir / "firmware.asm"

        if not Path(entry_point).exists():
            console.print(f"[red]Entry point '{entry_point}' not found.[/red]")
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
                compiler.compile(entry_point, str(output_file), chip, freq, config_map, verbose=verbose)
            except RuntimeError as e:
                progress.stop()
                console.print(f"[bold red]Compilation Error:[/bold red] {e}")
                raise typer.Exit(code=1)
                
            progress.update(build_task, completed=50)
            
            progress.update(build_task, completed=50)
            
            # Step 1.5: Library Injection (Float Support)
            with open(output_file, "r") as asm_f:
                asm_content = asm_f.read()
            
            if '#include "float.inc"' in asm_content:
                progress.update(build_task, description="Injecting Float Library...")
                import importlib.util
                
                # Locate pymcu.math package
                spec = importlib.util.find_spec("pymcu.math")
                if spec and spec.origin:
                    math_lib_path = Path(spec.origin).parent
                    
                    # Determine Architecture
                    arch = "pic16" # Default for PIC10/12/16
                    if chip.lower().startswith("pic18"):
                        arch = "pic18"
                        
                    src_inc = math_lib_path / arch / "float.inc"
                    dst_inc = output_dir / "float.inc"
                    
                    if src_inc.exists():
                        shutil.copy(str(src_inc), str(dst_inc))
                    else:
                        console.print(f"[bold yellow]Warning:[/bold yellow] float.inc not found for {arch}")
                else:
                    console.print("[bold yellow]Warning:[/bold yellow] pymcu-stdlib not installed, float operations may fail.")

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
            
            # Step 3: Cleanup
            progress.update(build_task, description="Cleaning up...")
            
            # Move extra files to dist/debug
            debug_dir = output_dir / "debug"
            for ext in [".lst", ".cod", ".map", ".asm"]: # Added .map common for linkers
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
