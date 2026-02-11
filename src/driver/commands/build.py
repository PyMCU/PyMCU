from pathlib import Path
import tomlkit
from ..toolchain import Toolchain
import typer
import os
from rich.console import Console
from rich.progress import Progress, SpinnerColumn, TextColumn, BarColumn, TimeElapsedColumn

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

        # Step 0: Ensure toolchain is ready (Interactive Check)
        # We do this before the progress bar to avoid visual glitches with prompts/downloads
        toolchain_config = pymcu_config.get("toolchain", {})
        assembler_override = toolchain_config.get("assembler")
        
        target_tool = Toolchain.resolve_tool_name(chip, assembler_override)
        if target_tool:
            # This triggers interactive prompt/install if missing
            try:
                Toolchain.prepare_tool(target_tool)
            except RuntimeError as e:
                console.print(f"[red]Toolchain preparation failed: {e}[/red]")
                raise typer.Exit(code=1)

        with Progress(
            SpinnerColumn(),
            TextColumn("[progress.description]{task.description}"),
            BarColumn(),
            TimeElapsedColumn(),
            transient=False,
        ) as progress:
            
            build_task = progress.add_task(description=f"Building {chip}...", total=100)
            
            # Step 1: Compilation
            progress.update(build_task, description="Compiling Python to ASM...", completed=10)
            Toolchain.run_compiler(entry_point, str(output_file), chip, freq, config_map, verbose=verbose)
            progress.update(build_task, completed=50)
            
            # Step 2: Assembly
            progress.update(build_task, description="Assembling...", completed=60)
            Toolchain.run_assembler(chip, str(output_file), assembler_override)
            progress.update(build_task, completed=90)
            
            # Step 3: Cleanup
            progress.update(build_task, description="Cleaning up...")
            
            # Move extra files to dist/debug to not "bother" the user
            debug_dir = output_dir / "debug"
            for ext in [".lst", ".cod", ".asm"]:
                f = output_file.with_suffix(ext)
                if f.exists():
                    if not debug_dir.exists():
                        debug_dir.mkdir(parents=True)
                    # Move to debug dir
                    import shutil
                    shutil.move(str(f), str(debug_dir / f.name))
            
            progress.update(build_task, description="Done!", completed=100)

        console.print(f"[bold green]Build successful![/bold green] Artifacts in: [blue]{output_dir}[/blue]")

    except Exception as e:
        console.print(f"[bold red]Error:[/bold red] {e}")
        raise typer.Exit(code=1)
