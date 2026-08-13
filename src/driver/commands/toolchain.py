# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
#
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

import os
import shutil
from pathlib import Path

import typer
from rich.console import Console
from rich.table import Table
from rich import box

from ..toolchains import discover_plugins

console = Console()

toolchain_app = typer.Typer(
    name="toolchain",
    help="Manage PyMCU toolchains (assemblers / compilers).",
    no_args_is_help=True,
)

# ---------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------

@toolchain_app.command("list")
def toolchain_list():
    """List all installed toolchain plugins and their installation status."""
    plugins = discover_plugins()

    if not plugins:
        console.print(
            "[yellow]No toolchain plugins installed.[/yellow]\n"
            'Install one with:  [bold]pip install "pymcu-compiler\\[avr]"[/bold]  '
            'or  [bold]pip install "pymcu-compiler\\[pic]"[/bold]'
        )
        return

    table = Table(title="PyMCU Toolchains", box=box.ROUNDED)
    table.add_column("Family", style="cyan", no_wrap=True)
    table.add_column("Description", style="white")
    table.add_column("Version", style="magenta")
    table.add_column("Status", style="bold")

    for family, plugin_cls in plugins.items():
        tc = plugin_cls.get_instance(console)
        status = "[green]installed[/green]" if tc.is_cached() else "[dim]not installed[/dim]"
        table.add_row(family, plugin_cls.description, plugin_cls.version, status)

    console.print(table)


@toolchain_app.command("install")
def toolchain_install(
    family: str = typer.Argument(
        ...,
        help="Toolchain family to install (e.g. avr, pic).",
    ),
):
    """
    Install a toolchain into the local cache (~/.pymcu/tools/).

    Examples
    --------
        pymcu toolchain install avr
        pymcu toolchain install pic
    """
    plugins = discover_plugins()

    if family not in plugins:
        if plugins:
            console.print(
                f"[red]Unknown toolchain family: {family!r}. "
                f"Installed plugins: {', '.join(plugins)}[/red]"
            )
        else:
            console.print(
                f"[red]No toolchain plugins installed.[/red]\n"
                f"Install one first, e.g.:  [bold]pip install pymcu[{family}][/bold]"
            )
        raise typer.Exit(code=1)

    plugin_cls = plugins[family]
    tc = plugin_cls.get_instance(console)

    if tc.is_cached():
        console.print(
            f"[green]Toolchain '{family}' (v{plugin_cls.version}) is already installed.[/green]"
        )
        return

    try:
        tc.install()
        console.print(f"[bold green]Toolchain '{family}' installed successfully.[/bold green]")
    except RuntimeError as e:
        console.print(f"[bold red]Installation failed:[/bold red] {e}")
        raise typer.Exit(code=1)


@toolchain_app.command("update")
def toolchain_update(
    family: str = typer.Argument(
        ...,
        help="Toolchain family to update (e.g. avr, pic).",
    ),
):
    """
    Re-download and reinstall a toolchain to pick up a newer version.

    Examples
    --------
        pymcu toolchain update avr
    """
    plugins = discover_plugins()

    if family not in plugins:
        if plugins:
            console.print(
                f"[red]Unknown toolchain family: {family!r}. "
                f"Installed plugins: {', '.join(plugins)}[/red]"
            )
        else:
            console.print(
                f"[red]No toolchain plugins installed.[/red]\n"
                f"Install one first, e.g.:  [bold]pip install pymcu[{family}][/bold]"
            )
        raise typer.Exit(code=1)

    plugin_cls = plugins[family]
    tc = plugin_cls.get_instance(console)

    # Force reinstall by wiping the version file so is_cached() returns False.
    version_file = tc._version_file()
    if version_file.exists():
        version_file.unlink()

    try:
        tc.install()
        console.print(
            f"[bold green]Toolchain '{family}' updated to v{plugin_cls.version}.[/bold green]"
        )
    except RuntimeError as e:
        console.print(f"[bold red]Update failed:[/bold red] {e}")
        raise typer.Exit(code=1)


@toolchain_app.command("clean")
def toolchain_clean(
    all_versions: bool = typer.Option(
        False, "--all",
        help="Remove every cached toolchain, including the ones in use. "
             "Without this, only superseded versions and stale layouts go.",
    ),
    dry_run: bool = typer.Option(
        False, "--dry-run", help="List what would be removed and exit."
    ),
):
    """
    Reclaim space in the toolchain cache (~/.pymcu/tools).

    Nothing pruned this cache until now, so every upgrade left its predecessor
    on disk indefinitely -- a developer machine can carry several gigabytes of
    superseded toolchains without noticing.

    By default this keeps the two newest versions of each toolchain, so a
    project pinned to the previous release still works, and removes older ones
    plus directories left by earlier cache layouts. Use --all to empty it
    entirely; anything still needed is re-downloaded on the next build.

    Examples
    --------
        pymcu toolchain clean --dry-run
        pymcu toolchain clean
        pymcu toolchain clean --all
    """
    root = _tools_root()
    if not root.is_dir():
        console.print(f"[dim]Nothing to clean: {root} does not exist.[/dim]")
        return

    targets = _collect_clean_targets(root, all_versions=all_versions)
    if not targets:
        console.print("[green]Toolchain cache is already tidy.[/green]")
        return

    total = 0
    table = Table(box=box.SIMPLE)
    table.add_column("Path")
    table.add_column("Size", justify="right")
    table.add_column("Why")
    for path, reason in targets:
        size = _dir_size(path)
        total += size
        table.add_row(str(path.relative_to(root)), _human(size), reason)
    console.print(table)

    if dry_run:
        console.print(f"[dim]Would free {_human(total)}. Run without --dry-run to remove.[/dim]")
        return

    freed = 0
    for path, _ in targets:
        size = _dir_size(path)
        try:
            shutil.rmtree(path)
            freed += size
        except OSError as e:
            console.print(f"[yellow]Could not remove {path}:[/yellow] {e}")

    console.print(f"[bold green]Freed {_human(freed)}.[/bold green]")


def _tools_root() -> Path:
    env = os.environ.get("PYMCU_TOOLS_DIR")
    return Path(env).resolve() if env else Path.home() / ".pymcu" / "tools"


def _collect_clean_targets(root: Path, *, all_versions: bool) -> list[tuple[Path, str]]:
    """
    Decide what can go, newest-first within each toolchain.

    Conservative on purpose: a directory is only dropped when it is clearly
    superseded, or when the user asked for everything.
    """
    from pymcu.toolchain.sdk import _default_platform_key

    current_key = _default_platform_key()
    targets: list[tuple[Path, str]] = []

    for platform_dir in sorted(p for p in root.iterdir() if p.is_dir()):
        # Directories from earlier key layouts (e.g. plain "darwin", or a key
        # naming an architecture whose binaries were never architecture-specific)
        # are dead weight: nothing looks there any more.
        if platform_dir.name != current_key and "-" not in platform_dir.name:
            targets.append((platform_dir, "stale cache layout"))
            continue

        for tool_dir in sorted(p for p in platform_dir.iterdir() if p.is_dir()):
            versions = sorted(
                (p for p in tool_dir.iterdir() if p.is_dir()),
                key=lambda p: p.stat().st_mtime,
                reverse=True,
            )
            if all_versions:
                targets.append((tool_dir, "all versions requested"))
            elif len(versions) > 2:
                targets.extend((v, "superseded version") for v in versions[2:])

    return targets


def _dir_size(path: Path) -> int:
    total = 0
    for entry in path.rglob("*"):
        try:
            if entry.is_file() and not entry.is_symlink():
                total += entry.stat().st_size
        except OSError:
            pass
    return total


def _human(size: int) -> str:
    value = float(size)
    for unit in ("B", "KB", "MB", "GB"):
        if value < 1024 or unit == "GB":
            return f"{value:.0f} {unit}" if unit == "B" else f"{value:.1f} {unit}"
        value /= 1024
    return f"{value:.1f} GB"
