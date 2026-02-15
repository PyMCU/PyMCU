from pathlib import Path
import typer
from typing import Optional, List
import tomlkit
import json
import subprocess
import sys
from rich.console import Console
from rich.panel import Panel
from rich.prompt import Prompt, Confirm

# Import factory for toolchain auto-detection
from ..toolchains import get_toolchain_for_chip

console = Console()

def get_available_chips() -> List[str]:
    """
    Dynamically scans the installed 'pymcu-stdlib' package for chip definitions.
    Returns a list of chip names (e.g., ['pic16f84a', 'pic16f877a']).
    """
    try:
        import pymcu
        if hasattr(pymcu, '__file__') and pymcu.__file__:
            chips_dir = Path(pymcu.__file__).parent / "chips"
            if chips_dir.is_dir():
                # List .py files, ignore __init__.py
                chips = [
                    f.stem for f in chips_dir.glob("*.py")
                    if f.name != "__init__.py"
                ]
                return sorted(chips)
    except ImportError:
        pass
    except Exception as e:
        console.print(f"[yellow]Warning: Could not scan for chips: {e}[/yellow]")

    return []

def new(name: str, mcu: Optional[str] = typer.Option(None, "--mcu", "-m", help="Target MCU (e.g., pic16f84a)")):
    console.print(Panel(f"[bold blue]Scaffolding new pymcu project: [green]{name}[/green][/bold blue]"))

    project_path = Path(name)
    if project_path.exists():
        console.print(f"[red]Error: Directory '{name}' already exists.[/red]")
        raise typer.Exit(code=1)

    chip = mcu
    if not chip:
        available_chips = get_available_chips()
        if available_chips:
            chip = Prompt.ask("Target MCU", choices=available_chips, default="pic16f84a")
        else:
            chip = Prompt.ask("Target MCU", default="pic16f84a")

    try:
        toolchain_instance = get_toolchain_for_chip(chip, console)
        toolchain_name = toolchain_instance.get_name()
    except ValueError:
        # Fallback if unknown chip, default to gputils or warn
        console.print(f"[yellow]Warning: No specific toolchain known for '{chip}'. Defaulting to 'gputils'.[/yellow]")
        toolchain_name = "gputils"

    # 3. Package Manager Selection
    pkg_manager = Prompt.ask(
        "Which package manager would you like to use?",
        choices=["uv", "pip", "poetry"],
        default="uv"
    )

    use_src = Confirm.ask("Use 'src' directory layout?", default=True)
    sources_dir = "src" if use_src else "."
    entry_file = "main.py" if use_src else "app.py"

    freq = 4000000

    try:
        project_path.mkdir(parents=True)

        if use_src:
            (project_path / sources_dir).mkdir(parents=True)

        # Generate configuration based on package manager
        if pkg_manager == "uv" or pkg_manager == "poetry":
            # For uv and poetry we use pyproject.toml
            # Use tomlkit to build the structure correctly
            doc = tomlkit.document()

            project = tomlkit.table()
            project.add("name", name)
            project.add("version", "0.1.0")

            deps = tomlkit.array()
            deps.append("pymcu-stdlib")
            # Pin the compiler version to the one currently running to ensure reproducibility
            try:
                from importlib.metadata import version
                current_version = version("pymcu-compiler")
                deps.append(f"pymcu-compiler=={current_version}")
            except Exception:
                # Fallback if running from source or version not found
                console.print("[yellow]Warning: Could not detect pymcu-compiler version. Adding unpinned dependency.[/yellow]")
                deps.append("pymcu-compiler")

            project.add("dependencies", deps)

            doc.add("project", project)

            if pkg_manager == "uv":
                uv_index = tomlkit.table()
                uv_index.add("name", "gitea")
                uv_index.add("url", "https://gitea.begeistert.dev/api/packages/begeistert/pypi/simple")
                uv_index.add("explicit", True)

                uv_indices = tomlkit.aot()
                uv_indices.append(uv_index)

                tool_uv = tomlkit.table()
                tool_uv.add("index", uv_indices)

                sources = tomlkit.table()
                pymcu_stdlib_source = tomlkit.inline_table()
                pymcu_stdlib_source.update({"index": "gitea"})
                sources.add("pymcu-stdlib", pymcu_stdlib_source)
                tool_uv.add("sources", sources)

                tool = tomlkit.table()
                tool.add("uv", tool_uv)
                doc.add("tool", tool)

            elif pkg_manager == "poetry":
                # Poetry uses [[tool.poetry.source]]
                poetry_source = tomlkit.table()
                poetry_source.add("name", "gitea")
                poetry_source.add("url", "https://gitea.begeistert.dev/api/packages/begeistert/pypi/simple")
                poetry_source.add("priority", "supplemental")

                poetry_sources = tomlkit.aot()
                poetry_sources.append(poetry_source)

                tool_poetry = tomlkit.table()
                tool_poetry.add("source", poetry_sources)

                if "tool" not in doc:
                    doc.add("tool", tomlkit.table())
                doc["tool"].add("poetry", tool_poetry)

            # Pymcu specific config
            pymcu_tool = tomlkit.table()
            pymcu_tool.add("chip", chip)
            pymcu_tool.add("frequency", freq)

            pymcu_config = tomlkit.table()
            pymcu_config.add(tomlkit.comment("FOSC = \"HS\""))
            pymcu_tool.add("config", pymcu_config)

            # Auto-detected toolchain
            pymcu_toolchain = tomlkit.table()
            pymcu_toolchain.add("name", toolchain_name)
            pymcu_tool.add("toolchain", pymcu_toolchain)

            if "tool" not in doc:
                doc.add("tool", tomlkit.table())

            doc["tool"].add("pymcu", pymcu_tool)

            with open(project_path / "pyproject.toml", "w") as f:
                f.write(tomlkit.dumps(doc))

        else: # pip
            # For pip, we'll create a simple pyproject.toml for pymcu config
            # and a requirements.txt for dependencies
            doc = tomlkit.document()
            tool = tomlkit.table()
            pymcu_tool = tomlkit.table()
            pymcu_tool.add("chip", chip)
            pymcu_tool.add("frequency", freq)
            pymcu_tool.add("config", tomlkit.table())

            pymcu_toolchain = tomlkit.table()
            pymcu_toolchain.add("name", toolchain_name)
            pymcu_tool.add("toolchain", pymcu_toolchain)

            tool.add("pymcu", pymcu_tool)
            doc.add("tool", tool)

            with open(project_path / "pyproject.toml", "w") as f:
                f.write(tomlkit.dumps(doc))

            requirements_content = "--extra-index-url https://gitea.begeistert.dev/api/packages/begeistert/pypi/simple\npymcu-stdlib\n"

            try:
                from importlib.metadata import version
                curr_ver = version("pymcu-compiler")
                requirements_content += f"pymcu-compiler=={curr_ver}\n"
            except Exception:
                requirements_content += "pymcu-compiler\n"

            with open(project_path / "requirements.txt", "w") as f:
                f.write(requirements_content)

        # 4. Generate VS Code Tasks
        vscode_dir = project_path / ".vscode"
        vscode_dir.mkdir()
        tasks_json = {
            "version": "2.0.0",
            "tasks": [
                {
                    "label": "pymcu: build",
                    "type": "shell",
                    "command": "pymcu build",
                    "group": {
                        "kind": "build",
                        "isDefault": True
                    },
                    "problemMatcher": []
                },
                {
                    "label": "pymcu: clean",
                    "type": "shell",
                    "command": "pymcu clean",
                    "problemMatcher": []
                },
                {
                    "label": "pymcu: flash",
                    "type": "shell",
                    "command": "pymcu flash",
                    "problemMatcher": []
                }
            ]
        }
        with open(vscode_dir / "tasks.json", "w") as f:
            json.dump(tasks_json, f, indent=4)

        # .gitignore
        gitignore_content = """
__pycache__/
dist/
*.hex
*.cod
*.lst
.venv/
.vscode/
"""
        with open(project_path / ".gitignore", "w") as f:
            f.write(gitignore_content)

        # src/main.py
        main_py_content = f"from pymcu.chips.{chip} import *\n\ndef main():\n    PORTB[RB0] = 1\n"
        with open(project_path / "src" / "main.py", "w") as f:
            f.write(main_py_content)

        if Confirm.ask("Initialize git repository?", default=True):
            try:
                subprocess.run(["git", "init"], cwd=project_path, check=True)
            except subprocess.CalledProcessError as e:
                console.print(f"[red]Failed to initialize git repository:[/red] {e}")
                raise typer.Exit(code=1)

        if Confirm.ask(f"Install dependencies with {pkg_manager} now?", default=True):
            with console.status(f"[bold green]Installing dependencies via {pkg_manager}..."):
                if pkg_manager == "uv":
                    subprocess.run(["uv", "sync"], cwd=project_path, check=True)
                elif pkg_manager == "poetry":
                    subprocess.run(["poetry", "install"], cwd=project_path, check=True)
                elif pkg_manager == "pip":
                    subprocess.run([sys.executable, "-m", "venv", ".venv"], cwd=project_path)
                    pip_cmd = [str(project_path / ".venv/bin/pip"), "install", "-r", "requirements.txt"]
                    subprocess.run(pip_cmd, cwd=project_path, check=True)

        console.print(f"[bold green]✓[/bold green] Project '[bold]{name}[/bold]' created successfully!")
        console.print(f"[blue]Target MCU:[/blue] {chip}")
        console.print(f"[blue]Toolchain:[/blue] {toolchain_name}")
        console.print(f"[blue]Package Manager:[/blue] {pkg_manager}")
        console.print(f"[dim]VS Code tasks created in .vscode/tasks.json[/dim]")

    except Exception as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(code=1)
