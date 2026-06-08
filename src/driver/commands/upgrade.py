# -----------------------------------------------------------------------------
# PyMCU CLI Driver — `pymcu upgrade`
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------

from __future__ import annotations

import shutil
import subprocess
import sys
from pathlib import Path
from typing import Optional

import typer
from rich.console import Console
from rich.table import Table

console = Console()


def _uv_bin() -> Optional[str]:
    return shutil.which("uv")


def _pymcu_packages_in_project() -> list[str]:
    """Return pymcu dependency package names declared in the project's pyproject.toml."""
    try:
        import tomlkit
        doc = tomlkit.loads(Path("pyproject.toml").read_text(encoding="utf-8"))
        deps: list[str] = list(doc.get("project", {}).get("dependencies", []))
        pkgs = []
        for dep in deps:
            name = dep.split("[")[0].split(">=")[0].split("==")[0].split(">")[0].strip().lower()
            if name.startswith("pymcu"):
                pkgs.append(name)
        return pkgs
    except Exception:
        return ["pymcu-avr", "pymcu-arm", "pymcu-stdlib"]


def _upgrade_project_venv(packages: list[str], check: bool, pre: bool) -> bool:
    """Upgrade pymcu packages in the project venv. Returns True if anything changed."""
    uv = _uv_bin()
    venv = Path(".venv")
    if not venv.exists():
        console.print("[yellow]No .venv found in current directory — nothing to upgrade.[/yellow]")
        return False

    if check:
        console.print("[dim]Project venv packages:[/dim]")
        for pkg in packages:
            try:
                from importlib.metadata import version
                console.print(f"  {pkg}=={version(pkg)}")
            except Exception:
                console.print(f"  {pkg} (not installed)")
        return False

    console.print("[bold]Upgrading project backend packages...[/bold]")

    if uv:
        cmd = [uv, "pip", "install", "--python", str(venv), "--upgrade"] + packages
        if pre:
            cmd.append("--prerelease=allow")
    else:
        venv_python = venv / ("Scripts/python.exe" if sys.platform == "win32" else "bin/python")
        cmd = [str(venv_python), "-m", "pip", "install", "--upgrade"] + packages
        if pre:
            cmd.append("--pre")

    result = subprocess.run(cmd, capture_output=False)
    return result.returncode == 0


def _upgrade_uv_tool(check: bool, pre: bool) -> None:
    """Upgrade the pymcu-compiler uv tool itself."""
    uv = _uv_bin()
    if not uv:
        console.print(
            "[dim]uv not found — to upgrade pymcu-compiler run:[/dim]\n"
            "  pip install --upgrade pymcu-compiler"
        )
        return

    # Check if pymcu-compiler is managed as a uv tool
    result = subprocess.run([uv, "tool", "list"], capture_output=True, text=True)
    if "pymcu-compiler" not in result.stdout:
        console.print(
            "[dim]pymcu-compiler is not a uv tool — to upgrade run:[/dim]\n"
            "  pip install --upgrade pymcu-compiler"
        )
        return

    if check:
        console.print("[dim]pymcu-compiler is managed as a uv tool[/dim]")
        return

    console.print("[bold]Upgrading pymcu-compiler tool...[/bold]")
    cmd = [uv, "tool", "upgrade", "pymcu-compiler"]
    if pre:
        cmd.append("--pre")
    subprocess.run(cmd)


def upgrade(
    check: bool = typer.Option(
        False, "--check",
        help="Report available updates without installing anything.",
    ),
    no_tool: bool = typer.Option(
        False, "--no-tool",
        help="Skip upgrading the global pymcu-compiler uv tool.",
    ),
    pre: bool = typer.Option(
        True, "--pre/--no-pre",
        help="Include pre-release versions (default: on, PyMCU is in alpha).",
    ),
):
    """Upgrade pymcu backend packages in this project and the global pymcu-compiler tool."""
    if check:
        console.print("[bold blue]Checking for updates...[/bold blue]")
    else:
        console.print("[bold blue]Upgrading pymcu packages...[/bold blue]")

    # ── 1. Project venv ───────────────────────────────────────────────
    pkgs = _pymcu_packages_in_project()
    if pkgs:
        _upgrade_project_venv(pkgs, check=check, pre=pre)
    else:
        console.print("[dim]No pymcu packages found in pyproject.toml.[/dim]")

    # ── 2. Global pymcu-compiler tool ─────────────────────────────────
    if not no_tool:
        _upgrade_uv_tool(check=check, pre=pre)

    if not check:
        console.print("\n[bold green]Done![/bold green] Run [bold]pymcu build[/bold] to verify.")
