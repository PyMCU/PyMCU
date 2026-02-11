from pathlib import Path
import tomlkit
import typer
from rich.console import Console
from ..programmers import get_programmer

console = Console()

def flash(verbose: bool = typer.Option(False, "--verbose", "-v", help="Enable verbose logging")):
    """
    Flashes the built firmware to the target microcontroller.
    """
    pyproject_path = Path("pyproject.toml")
    if not pyproject_path.exists():
        console.print("[red]No pyproject.toml found. Are you in a pymcu project?[/red]")
        raise typer.Exit(code=1)

    try:
        # 1. Read Project Config
        with open(pyproject_path, "r") as f:
            config = tomlkit.load(f)

        pymcu_config = config.get("tool", {}).get("pymcu", {})
        chip = pymcu_config.get("chip", "pic16f84a") 
        # Default to pickit2 if not specified
        programmer_name = pymcu_config.get("programmer", "pickit2")

        # 2. Check for Artifacts
        dist_dir = Path("dist")
        hex_file = dist_dir / "firmware.hex"
        
        if not hex_file.exists():
             console.print("[red]Firmware file 'dist/firmware.hex' not found.[/red]")
             console.print("Please run [bold]pymcu build[/bold] first.")
             raise typer.Exit(code=1)

        # 3. Get Programmer
        try:
            programmer = get_programmer(programmer_name, console)
        except ValueError as e:
            console.print(f"[red]Error:[/red] {e}")
            raise typer.Exit(code=1)

        # 4. Check Cache / Install
        if not programmer.is_cached():
            try:
                programmer.install()
            except RuntimeError as e:
                console.print(f"[bold red]Programmer installation failed:[/bold red] {e}")
                raise typer.Exit(code=1)

        # 5. Flash
        try:
            programmer.flash(hex_file, chip)
        except RuntimeError as e:
            console.print(f"[red]Flash Failed:[/red] {e}")
            raise typer.Exit(code=1)
            
    except Exception as e:
        console.print(f"[bold red]Unexpected Error:[/bold red] {e}")
        raise typer.Exit(code=1)
