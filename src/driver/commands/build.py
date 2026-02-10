from pathlib import Path
import tomlkit
from ..toolchain import Toolchain
import typer

def build():
    pyproject_path = Path("pyproject.toml")
    if not pyproject_path.exists():
        typer.echo("No pyproject.toml found. Are you in a pymcu project?", err=True)
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
            typer.echo(f"Entry point '{entry_point}' not found.", err=True)
            raise typer.Exit(code=1)

        if not output_dir.exists():
            output_dir.mkdir(parents=True)

        typer.echo(f"[pymcu] Building project for {chip} ({freq/1000000:.1f}MHz)...")
        
        Toolchain.run_compiler(entry_point, str(output_file), chip, freq, config_map)
        
        typer.echo(f"[pymcu] Build successful! Artifact: {output_file}")

    except Exception as e:
        typer.echo(f"Error: {e}", err=True)
        raise typer.Exit(code=1)
