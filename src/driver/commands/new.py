from pathlib import Path
import typer
from typing import Optional
import tomlkit
from rich.console import Console
from rich.panel import Panel
from rich.prompt import Prompt, Confirm

console = Console()

def new(name: str, mcu: Optional[str] = typer.Option(None, "--mcu", "-m", help="Target MCU (e.g., pic16f84a)")):
    console.print(Panel(f"[bold blue]Scaffolding new pymcu project: [green]{name}[/green][/bold blue]"))
    
    project_path = Path(name)
    if project_path.exists():
        console.print(f"[red]Error: Directory '{name}' already exists.[/red]")
        raise typer.Exit(code=1)

    # MCU Selection
    chip = mcu
    if not chip:
        chip = Prompt.ask("Target MCU", default="pic16f84a")

    # Package Manager Selection
    pkg_manager = Prompt.ask(
        "Which package manager would you like to use?",
        choices=["uv", "pip", "poetry"],
        default="uv"
    )

    freq = 4000000

    try:
        (project_path / "src").mkdir(parents=True)

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
            
            pymcu_toolchain = tomlkit.table()
            pymcu_toolchain.add(tomlkit.comment("assembler = \"gpasm\""))
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
            pymcu_tool.add("toolchain", tomlkit.table())
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

        # src/main.py
        main_py_content = f"from pymcu.chips.{chip} import *\n\ndef main():\n    PORTB[RB0] = 1\n"
        with open(project_path / "src" / "main.py", "w") as f:
            f.write(main_py_content)

        console.print(f"[bold green]✓[/bold green] Project '[bold]{name}[/bold]' created successfully!")
        console.print(f"[blue]Target MCU:[/blue] {chip}")
        console.print(f"[blue]Package Manager:[/blue] {pkg_manager}")

    except Exception as e:
        console.print(f"[red]Error:[/red] {e}")
        raise typer.Exit(code=1)
