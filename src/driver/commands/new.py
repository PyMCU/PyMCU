# -----------------------------------------------------------------------------
# Whisnake CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the Whisnake Project Authors
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
    Dynamically scans the installed 'whisnake-stdlib' package for chip definitions.
    Returns a list of chip names (e.g., ['pic16f84a', 'pic16f877a']).
    """
    try:
        import whisnake
        if hasattr(whisnake, '__file__') and whisnake.__file__:
            chips_dir = Path(whisnake.__file__).parent / "chips"
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

def new(name: str):
    console.print(Panel(f"[bold blue]Scaffolding new whip project: [green]{name}[/green][/bold blue]"))

    project_path = Path(name)
    if project_path.exists():
        console.print(f"[red]Error: Directory '{name}' already exists.[/red]")
        raise typer.Exit(code=1)

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
            deps.append("whisnake-stdlib")
            # Pin the compiler version to the one currently running to ensure reproducibility
            try:
                from importlib.metadata import version
                current_version = version("whip-compiler")
                deps.append(f"whip-compiler=={current_version}")
            except Exception:
                # Fallback if running from source or version not found
                console.print("[yellow]Warning: Could not detect whip-compiler version. Adding unpinned dependency.[/yellow]")
                deps.append("whip-compiler")

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
                whisnake_stdlib_source = tomlkit.inline_table()
                whisnake_stdlib_source.update({"index": "gitea"})
                sources.add("whisnake-stdlib", whisnake_stdlib_source)
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

            # Whip specific config
            whip_tool = tomlkit.table()
            whip_tool.add("chip", chip)
            whip_tool.add("frequency", freq)
            whip_tool.add("sources", sources_dir)
            whip_tool.add("entry", entry_file)

            whip_config = tomlkit.table()
            whip_config.add(tomlkit.comment("FOSC = \"HS\""))
            whip_tool.add("config", whip_config)

            # Auto-detected toolchain
            whip_toolchain = tomlkit.table()
            whip_toolchain.add("name", toolchain_name)
            whip_tool.add("toolchain", whip_toolchain)

            # Programmer configuration
            whip_programmer = tomlkit.table()
            whip_programmer.add("name", "pickit2")
            whip_tool.add("programmer", whip_programmer)

            if "tool" not in doc:
                doc.add("tool", tomlkit.table())

            doc["tool"].add("whip", whip_tool)

            with open(project_path / "pyproject.toml", "w") as f:
                f.write(tomlkit.dumps(doc))

        else: # pip
            # For pip, we'll create a simple pyproject.toml for whip config
            # and a requirements.txt for dependencies
            doc = tomlkit.document()
            tool = tomlkit.table()
            whip_tool = tomlkit.table()
            whip_tool.add("chip", chip)
            whip_tool.add("frequency", freq)
            whip_tool.add("sources", sources_dir)
            whip_tool.add("entry", entry_file)
            whip_tool.add("config", tomlkit.table())

            whip_toolchain = tomlkit.table()
            whip_toolchain.add("name", toolchain_name)
            whip_tool.add("toolchain", whip_toolchain)

            whip_programmer = tomlkit.table()
            whip_programmer.add("name", "pickit2")
            whip_tool.add("programmer", whip_programmer)

            tool.add("whip", whip_tool)
            doc.add("tool", tool)

            with open(project_path / "pyproject.toml", "w") as f:
                f.write(tomlkit.dumps(doc))

            requirements_content = "--extra-index-url https://gitea.begeistert.dev/api/packages/begeistert/pypi/simple\nwhisnake-stdlib\n"

            try:
                from importlib.metadata import version
                curr_ver = version("whip-compiler")
                requirements_content += f"whip-compiler=={curr_ver}\n"
            except Exception:
                requirements_content += "whip-compiler\n"

            with open(project_path / "requirements.txt", "w") as f:
                f.write(requirements_content)

        # 4. Generate VS Code Tasks
        vscode_dir = project_path / ".vscode"
        vscode_dir.mkdir()
        tasks_json = {
            "version": "2.0.0",
            "tasks": [
                {
                    "label": "whip: build",
                    "type": "shell",
                    "command": "whip build",
                    "group": {
                        "kind": "build",
                        "isDefault": True
                    },
                    "problemMatcher": ["$whipc"]
                },
                {
                    "label": "whip: clean",
                    "type": "shell",
                    "command": "whip clean",
                    "problemMatcher": []
                },
                {
                    "label": "whip: flash",
                    "type": "shell",
                    "command": "whip flash",
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

        # Entry point file
        main_py_content = f"from whisnake.chips.{chip} import *\n\ndef main():\n    PORTB[RB0] = 1\n"
        entry_dir = project_path / sources_dir if use_src else project_path
        with open(entry_dir / entry_file, "w") as f:
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
