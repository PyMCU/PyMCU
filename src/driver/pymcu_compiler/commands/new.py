from pathlib import Path
import typer
from typing import Optional

def new(name: str, mcu: Optional[str] = typer.Option(None, "--mcu", "-m", help="Target MCU (e.g., pic16f84a)")):
    project_path = Path(name)
    if project_path.exists():
        typer.echo(f"Directory '{name}' already exists.", err=True)
        raise typer.Exit(code=1)

    chip = mcu
    if not chip:
        chip = typer.prompt("Target MCU", default="pic16f84a")

    freq = 4000000

    try:
        (project_path / "src").mkdir(parents=True)

        # pyproject.toml
        pyproject_content = f"""[project]
name = "{name}"
version = "0.1.0"
dependencies = [
    "pymcu-stdlib",
]

[[tool.uv.index]]
name = "gitea"
url = "https://gitea.begeistert.dev/api/packages/begeistert/pypi/simple"
explicit = true

[tool.uv.sources]
pymcu-stdlib = {{index = "gitea"}}

[tool.pymcu]
chip = "{chip}"
frequency = {freq}

[tool.pymcu.config]
# FOSC = "HS"
"""
        with open(project_path / "pyproject.toml", "w") as f:
            f.write(pyproject_content)

        # src/main.py
        main_py_content = f"from pymcu.chips.{chip} import *\n\ndef main():\n    PORTB[RB0] = 1\n"
        with open(project_path / "src" / "main.py", "w") as f:
            f.write(main_py_content)

        typer.echo(f"[pymcu] Project '{name}' created successfully!")

    except Exception as e:
        typer.echo(f"Error: {e}", err=True)
        raise typer.Exit(code=1)
