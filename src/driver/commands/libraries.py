# -----------------------------------------------------------------------------
# PyMCU CLI Driver -- `pymcu install` / `uninstall` / `libraries` / `search`
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

"""
Library management for the current project.

PyPI carries the bytes; a curated index decides what exists.  PyPI is full of
Python that cannot compile for a microcontroller, and an install that "works"
and then breaks the build is worse than one that refuses -- so `pymcu install`
resolves names against the index at pymcu.org first, checks the target before
downloading anything, and only then hands the work to uv or pip.

Nothing is ever installed globally: a library the compiler cannot see in the
project's .venv might as well not exist.
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request
from importlib.metadata import PackageNotFoundError, version as dist_version
from pathlib import Path
from typing import Optional

import tomlkit
import typer
from rich.console import Console
from rich.table import Table

from ..core.boards import extension_board_chips, resolve_chip_for_board
from ..core.libraries import (
    LANGUAGE_LEVEL,
    Library,
    check_compatibility,
    chip_arch,
    discover_libraries,
    find_module_collisions,
    site_packages_of,
)

console = Console()

DEFAULT_INDEX_URL = "https://libraries.pymcu.org/index.json"

# Mirror on raw.githubusercontent, tried when the primary does not answer.
# This is not belt-and-braces: the pymcu.org zone runs Bot Fight Mode, which
# answers 403 to requests from data centres -- and `pymcu install` runs inside
# other people's CI. An index that only resolves from a laptop would break
# reproducible builds for everyone else.
MIRROR_INDEX_URL = (
    "https://raw.githubusercontent.com/PyMCU/pymcu-libraries/main/index.json"
)
CACHE_DIR = Path.home() / ".pymcu"
CACHE_FILE = CACHE_DIR / "libraries-index.json"
DISTRIBUTION_PREFIX = "pymcu-lib-"


# ---------------------------------------------------------------------------
# Index
# ---------------------------------------------------------------------------

def _index_urls() -> list[str]:
    """The URLs to try, in order. PYMCU_LIBRARY_INDEX overrides both."""
    override = os.environ.get("PYMCU_LIBRARY_INDEX")
    if override:
        return [override]
    return [DEFAULT_INDEX_URL, MIRROR_INDEX_URL]


def _index_url() -> str:
    return _index_urls()[0]


def _read_cached_index() -> dict | None:
    try:
        return json.loads(CACHE_FILE.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None


# Why the last attempt failed, for the caller to show. A module-level value
# rather than a return value so the existing (index, source) contract stays put:
# every command asks for it in the one place it reports the failure.
_LAST_INDEX_ERROR = ""


def last_index_error() -> str:
    """A human-readable reason the index could not be fetched, or ""."""
    return _LAST_INDEX_ERROR


def _ssl_context():
    """
    A context with certificates that actually verify, when we can build one.

    The python.org macOS builds ship without the system trust store until
    `Install Certificates.command` is run, so every HTTPS request fails with
    CERTIFICATE_VERIFY_FAILED -- which reads exactly like the index being down.
    certifi is not a dependency, but pip pulls it into most environments, and
    using it when present turns a dead end into a working command.
    """
    try:
        import ssl

        import certifi  # type: ignore

        return ssl.create_default_context(cafile=certifi.where())
    except Exception:
        return None


def _download_index(url: str) -> dict | None:
    global _LAST_INDEX_ERROR
    try:
        with urllib.request.urlopen(url, timeout=10) as response:
            return json.loads(response.read().decode("utf-8"))
    except urllib.error.URLError as exc:
        reason = getattr(exc, "reason", exc)
        if "CERTIFICATE_VERIFY_FAILED" in str(reason):
            context = _ssl_context()
            if context is not None:
                try:
                    with urllib.request.urlopen(url, timeout=10, context=context) as response:
                        return json.loads(response.read().decode("utf-8"))
                except Exception as retry_exc:
                    reason = retry_exc
            if "CERTIFICATE_VERIFY_FAILED" in str(reason):
                _LAST_INDEX_ERROR = (
                    "TLS certificate verification failed. This is a local trust "
                    "store problem, not the index being down: on a python.org "
                    "build for macOS, run "
                    "'/Applications/Python 3.x/Install Certificates.command', or "
                    "install certifi in this environment."
                )
                return None
        _LAST_INDEX_ERROR = f"{url}: {reason}"
        return None
    except (TimeoutError, json.JSONDecodeError, OSError, ValueError) as exc:
        _LAST_INDEX_ERROR = f"{url}: {exc}"
        return None


def fetch_index(refresh: bool = False) -> tuple[dict, str]:
    """
    Return (index, source) where source is "network", "cache" or "".

    A network failure is never fatal on its own: a cached index is worth far
    more than an aborted command, and the caller says where the data came from
    so a stale answer is never passed off as a fresh one.
    """
    global _LAST_INDEX_ERROR
    _LAST_INDEX_ERROR = ""

    cached = None if refresh else _read_cached_index()
    if cached is not None:
        return cached, "cache"

    for url in _index_urls():
        payload = _download_index(url)
        if payload is None:
            continue
        _LAST_INDEX_ERROR = ""
        try:
            CACHE_DIR.mkdir(parents=True, exist_ok=True)
            CACHE_FILE.write_text(json.dumps(payload), encoding="utf-8")
        except OSError:
            pass
        return payload, "network"

    fallback = _read_cached_index()
    if fallback is not None:
        return fallback, "cache"
    return {}, ""


def _entries(index: dict) -> list[dict]:
    entries = index.get("libraries", [])
    return [e for e in entries if isinstance(e, dict)]


def find_entry(index: dict, name: str) -> dict | None:
    """Look a library up by its short name or by its distribution name."""
    wanted = name.strip().lower()
    for entry in _entries(index):
        if wanted in (str(entry.get("name", "")).lower(),
                      str(entry.get("distribution", "")).lower()):
            return entry
    return None


def entry_verdict(entry: dict, chip: str, flavors: list[str]) -> list[str]:
    """Reasons an index entry cannot serve this target, from measured data."""
    reasons: list[str] = []
    arch = chip_arch(chip)

    status = str(entry.get("status", "active"))
    if status == "broken":
        reasons.append("the index reports it no longer builds with the current compiler")

    measured = entry.get("measured", {}).get("targets", {})
    if isinstance(measured, dict) and chip.lower() in measured:
        result = measured[chip.lower()]
        build = str(result.get("build", "")) if isinstance(result, dict) else str(result)
        if build and build != "ok":
            reasons.append(f"measured as '{build}' on {chip}")
    else:
        declared_arch = [str(a).lower() for a in entry.get("arch", [])]
        if declared_arch and arch and arch not in declared_arch:
            reasons.append(
                f"supports {', '.join(declared_arch)}; this project targets {chip} ({arch})"
            )

    layer = str(entry.get("layer", "native"))
    if layer != "native" and layer not in flavors:
        declared = ", ".join(flavors) if flavors else "none"
        reasons.append(
            f"is written against the {layer} layer, but this project declares "
            f"stdlib = [{declared}]"
        )

    level = int(entry.get("requires", {}).get("language_level", 1))
    if level > LANGUAGE_LEVEL:
        reasons.append(f"needs language level {level}; this driver provides {LANGUAGE_LEVEL}")

    return reasons


# ---------------------------------------------------------------------------
# JSON shaping (consumed by the IDE plugins; keep the keys stable)
# ---------------------------------------------------------------------------

def _entry_json(entry: dict, reasons: list[str], installed: set[str]) -> dict:
    """An index entry as the IDE plugins consume it."""
    distribution = str(entry.get("distribution", ""))
    return {
        "name": str(entry.get("name", "")),
        "distribution": distribution,
        "version": str(entry.get("version", "")),
        "summary": str(entry.get("summary", "")),
        "categories": [str(c) for c in entry.get("categories", [])],
        "layer": str(entry.get("layer", "native")),
        "arch": [str(a) for a in entry.get("arch", [])],
        "status": str(entry.get("status", "active")),
        "repository": str(entry.get("repository", "")),
        # Empty means "nothing stops this library serving the current target".
        "reasons": reasons,
        "fits": not reasons,
        "installed": distribution.lower() in installed,
    }


def _installed_json(lib: Library, project: Project) -> dict:
    """An installed library as the IDE plugins consume it."""
    reasons = (check_compatibility(lib, chip=project.chip, flavors=project.flavors)
               if project.chip else ["no board or target declared"])
    return {
        "name": lib.name,
        "distribution": lib.distribution,
        "version": lib.version,
        "summary": lib.summary,
        "modules": list(lib.modules),
        "categories": list(lib.categories),
        "layer": lib.layer,
        "repository": lib.repository,
        "reasons": reasons,
        "usable": not reasons,
    }


# ---------------------------------------------------------------------------
# Project context
# ---------------------------------------------------------------------------

class Project:
    """The project in the current directory: its config, chip and environment."""

    def __init__(self, path: Path, doc: tomlkit.TOMLDocument):
        self.path = path
        self.root = path.parent
        self.doc = doc
        cfg = doc.get("tool", {}).get("pymcu", {})
        self.flavors = [str(f) for f in cfg.get("stdlib", [])]
        board = cfg.get("board")
        target = cfg.get("target") or cfg.get("chip")
        if board:
            self.chip = resolve_chip_for_board(str(board), extension_board_chips(self.flavors)) or ""
        else:
            self.chip = str(target) if target else ""
        self.board = str(board) if board else ""

    @property
    def venv(self) -> Path:
        return self.root / ".venv"


def _installed_libraries(project: Project) -> tuple[list[Library], list[str]]:
    """
    The libraries visible to a build of this project.

    Read from the project's own .venv whenever it exists: the driver may be
    running from somewhere else entirely (pipx, a global install, or simply the
    moment right after installing into that .venv), and what matters is what the
    compiler will find, not what this interpreter happens to import.
    """
    import importlib

    importlib.invalidate_caches()
    if project.venv.exists():
        search = site_packages_of(project.venv)
        if search:
            return discover_libraries(search_path=search)
    return discover_libraries()


def _load_project() -> Project:
    path = Path("pyproject.toml")
    if not path.exists():
        console.print("[red]No pyproject.toml found. Are you in a pymcu project?[/red]")
        raise typer.Exit(code=1)
    return Project(path, tomlkit.loads(path.read_text(encoding="utf-8")))


def _require_chip(project: Project) -> str:
    if not project.chip:
        console.print(
            "[red]This project declares no board or target in \\[tool.pymcu].[/red]\n"
            "[dim]A library is only installable once we know which chip it has to serve.[/dim]"
        )
        raise typer.Exit(code=1)
    return project.chip


# ---------------------------------------------------------------------------
# Package manager plumbing
# ---------------------------------------------------------------------------

def _uv_bin() -> str | None:
    found = shutil.which("uv")
    if found:
        return found
    candidate = Path(sys.executable).parent / ("uv.exe" if sys.platform == "win32" else "uv")
    return str(candidate) if candidate.is_file() else None


def _venv_python(project: Project) -> Path:
    return project.venv / ("Scripts/python.exe" if sys.platform == "win32" else "bin/python")


def _uses_uv_add(project: Project) -> bool:
    """
    True when this project is managed by `uv add`.

    That command resolves, installs *and* records the dependency itself, and it
    creates the environment if there is none -- so when it applies, the driver
    must not also write to pyproject.toml or it would end up listed twice.
    """
    if _uv_bin() is None:
        return False
    return (project.root / "uv.lock").exists() or "project" in project.doc


def install_command(project: Project, distribution: str, *, pre: bool) -> list[str] | None:
    """The command that installs *distribution* into this project's environment."""
    uv = _uv_bin()
    if uv and _uses_uv_add(project):
        return [uv, "add", distribution] + (["--prerelease=allow"] if pre else [])
    if uv:
        return (
            [uv, "pip", "install", "--python", str(project.venv), distribution]
            + (["--prerelease=allow"] if pre else [])
        )

    python = _venv_python(project)
    if not python.exists():
        return None
    return [str(python), "-m", "pip", "install", distribution] + (["--pre"] if pre else [])


def uninstall_command(project: Project, distribution: str) -> list[str] | None:
    """The command that removes *distribution* from this project's environment."""
    uv = _uv_bin()
    if uv and _uses_uv_add(project):
        return [uv, "remove", distribution]
    if uv:
        return [uv, "pip", "uninstall", "--python", str(project.venv), distribution]

    python = _venv_python(project)
    if not python.exists():
        return None
    return [str(python), "-m", "pip", "uninstall", "-y", distribution]


def _needs_environment(project: Project) -> bool:
    """True when the project has no environment and nothing here would create one."""
    return not project.venv.exists() and not _uses_uv_add(project)


def _run(cmd: list[str], cwd: Path) -> bool:
    result = subprocess.run(cmd, cwd=cwd)
    return result.returncode == 0


# ---------------------------------------------------------------------------
# pyproject.toml editing
# ---------------------------------------------------------------------------

def _add_dependency(project: Project, requirement: str) -> None:
    """Record the dependency, preserving the file's existing formatting."""
    doc = project.doc
    if "project" not in doc:
        req_file = project.root / "requirements.txt"
        if req_file.exists():
            lines = req_file.read_text(encoding="utf-8").splitlines()
            name = requirement.split(">=")[0]
            lines = [ln for ln in lines if not ln.startswith(name)]
            lines.append(requirement)
            req_file.write_text("\n".join(lines) + "\n", encoding="utf-8")
            return
        console.print(
            "[yellow]No \\[project] table and no requirements.txt: the library is "
            "installed but not recorded as a dependency.[/yellow]"
        )
        return

    deps = doc["project"].get("dependencies")
    if deps is None:
        deps = tomlkit.array()
        doc["project"]["dependencies"] = deps

    name = requirement.split(">=")[0]
    for existing in list(deps):
        if str(existing).split(">=")[0].split("==")[0].strip() == name:
            deps.remove(existing)
    deps.append(requirement)
    project.path.write_text(tomlkit.dumps(doc), encoding="utf-8")


def _remove_dependency(project: Project, distribution: str) -> None:
    doc = project.doc
    deps = doc.get("project", {}).get("dependencies")
    if deps is None:
        return
    for existing in list(deps):
        if str(existing).split(">=")[0].split("==")[0].strip() == distribution:
            deps.remove(existing)
    project.path.write_text(tomlkit.dumps(doc), encoding="utf-8")


# ---------------------------------------------------------------------------
# Verification build
# ---------------------------------------------------------------------------

def _pymcu_executable() -> Path | None:
    candidate = Path(sys.executable).parent / ("pymcu.exe" if sys.platform == "win32" else "pymcu")
    if candidate.exists():
        return candidate
    found = shutil.which("pymcu")
    return Path(found) if found else None


def verify_example(lib: Library, project: Project) -> tuple[bool, str]:
    """
    Compile the library's example for this project's chip.

    The example is copied and retargeted rather than built in place: it ships
    pinned to whatever board its author used, and the only question worth
    answering here is whether it builds for *this* chip.
    """
    example = lib.example_dir()
    if example is None:
        return True, "no example shipped -- nothing to verify"

    pymcu = _pymcu_executable()
    if pymcu is None:
        return True, "pymcu executable not found -- skipped"

    with tempfile.TemporaryDirectory() as tmp:
        work = Path(tmp) / "example"
        shutil.copytree(example, work)

        config = work / "pyproject.toml"
        if not config.exists():
            return True, "example has no pyproject.toml -- skipped"

        doc = tomlkit.loads(config.read_text(encoding="utf-8"))
        pymcu_cfg = doc.setdefault("tool", tomlkit.table()).setdefault("pymcu", tomlkit.table())
        pymcu_cfg.pop("board", None)
        pymcu_cfg["target"] = project.chip
        if project.flavors:
            arr = tomlkit.array()
            for flavor in project.flavors:
                arr.append(flavor)
            pymcu_cfg["stdlib"] = arr
        config.write_text(tomlkit.dumps(doc), encoding="utf-8")

        result = subprocess.run(
            [str(pymcu), "build"], cwd=work, capture_output=True, text=True
        )
        if result.returncode == 0:
            return True, "example builds for this chip"

        detail = (result.stderr or result.stdout or "").strip().splitlines()
        return False, detail[-1] if detail else "build failed"


# ---------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------

def install(
    name: str = typer.Argument(..., help="Library name as listed in the PyMCU index."),
    from_pypi: bool = typer.Option(
        False, "--from-pypi",
        help="Skip the index and install this distribution straight from PyPI. "
             "The manifest is still required.",
    ),
    verify: bool = typer.Option(
        True, "--verify/--no-verify",
        help="Compile the library's example for this project's chip after installing.",
    ),
    refresh: bool = typer.Option(False, "--refresh", help="Re-download the index first."),
    pre: bool = typer.Option(
        True, "--pre/--no-pre",
        help="Allow pre-release versions (default: on, PyMCU is in alpha).",
    ),
):
    """Install a PyMCU library into this project."""
    project = _load_project()
    chip = _require_chip(project)

    distribution = name if from_pypi else ""
    entry: dict | None = None

    if not from_pypi:
        index, source = fetch_index(refresh=refresh)
        if not index:
            console.print(
                f"[red]Could not reach the library index at {_index_url()} and no cached "
                "copy is available.[/red]"
            )
            if last_index_error():
                console.print(f"[dim]{last_index_error()}[/dim]")
            console.print(
                "[dim]Retry when online, or use --from-pypi <distribution> if you know "
                "what you are installing.[/dim]"
            )
            raise typer.Exit(code=1)
        if source == "cache":
            console.print("[dim]Using the cached index (run with --refresh to update).[/dim]")

        entry = find_entry(index, name)
        if entry is None:
            console.print(
                f"[red]'{name}' is not in the PyMCU library index.[/red]\n"
                "[dim]PyMCU only installs libraries that are known to compile. To add one, "
                "open a PR against the pymcu-libraries repository.[/dim]\n"
                "[dim]To install it anyway: pymcu install --from-pypi <distribution>[/dim]"
            )
            raise typer.Exit(code=1)

        reasons = entry_verdict(entry, chip, project.flavors)
        if reasons:
            console.print(f"[red]'{name}' does not fit this project:[/red]")
            for reason in reasons:
                console.print(f"  - it {reason}")
            raise typer.Exit(code=1)

        if str(entry.get("status", "active")) == "unmaintained":
            console.print(
                "[yellow]Note:[/yellow] the index marks this library as unmaintained."
            )
        distribution = str(entry.get("distribution") or f"{DISTRIBUTION_PREFIX}{name}")

    known = {lib.name for lib in _installed_libraries(project)[0]}

    if _needs_environment(project):
        console.print(
            "[red]This project has no .venv, and nothing here can create one.[/red]\n"
            "[dim]Run `uv sync` (or python -m venv .venv) first: a library the "
            "compiler cannot see in the project's environment does nothing.[/dim]"
        )
        raise typer.Exit(code=1)

    cmd = install_command(project, distribution, pre=pre)
    if cmd is None:
        console.print(
            "[red]No .venv in this project and uv is not available.[/red]\n"
            "[dim]Create the environment first (uv sync, or python -m venv .venv).[/dim]"
        )
        raise typer.Exit(code=1)

    console.print(f"[bold]Installing[/bold] {distribution} ...")
    if not _run(cmd, project.root):
        console.print(f"[red]Installation of {distribution} failed.[/red]")
        raise typer.Exit(code=1)

    # Preflight against what actually landed on disk. The index can lag behind a
    # release; the manifest in the wheel cannot.  The package is found through
    # the entry point it registered, never by guessing an import name from the
    # distribution name -- those differ by design, and a local path has neither.
    installed, problems_found = _installed_libraries(project)
    lib = next((candidate for candidate in installed if candidate.name not in known), None)
    if lib is None and entry is not None:
        lib = next((c for c in installed if c.name == str(entry.get("name", ""))), None)

    if lib is None:
        detail = "; ".join(problems_found) if problems_found else (
            "it registers no pymcu.libraries entry point, so the compiler would "
            "never see it"
        )
        _rollback(project, distribution, detail)

    problems = check_compatibility(lib, chip=chip, flavors=project.flavors)
    problems.extend(
        collision for collision in find_module_collisions(installed)
        if lib.distribution in collision
    )
    if problems:
        _rollback(project, lib.distribution, "; ".join(problems))

    if verify:
        ok, detail = verify_example(lib, project)
        if not ok:
            _rollback(project, lib.distribution,
                      f"its example does not build for {chip}: {detail}")
        console.print(f"[dim]Verified: {detail}[/dim]")

    # `uv add` already recorded it; writing again would list it twice.
    if not _uses_uv_add(project):
        _add_dependency(project, f"{distribution}>={lib.version}")

    console.print(f"[bold green]+[/bold green] {lib.name} {lib.version} installed.")
    console.print(f"  import with: [bold]from {lib.modules[0]} import ...[/bold]")
    for flavor in project.flavors:
        if lib.adapter_dir(flavor) is not None:
            console.print(f"  using the [bold]{flavor}[/bold] adapter")
    if entry:
        _print_measured(entry, chip)


def _rollback(project: Project, distribution: str, reason: str) -> None:
    """
    Undo an install that turned out not to fit, then exit.

    `uv add` records the dependency before this code ever sees the package, so
    the rollback has to remove it from pyproject.toml too -- and say so, rather
    than claiming the file was left alone when it was not.
    """
    console.print(f"[red]{distribution} cannot be used here: {reason}[/red]")

    cmd = uninstall_command(project, distribution)
    if cmd is None:
        console.print(
            f"[yellow]Could not roll back automatically. Remove it with: "
            f"pip uninstall {distribution}[/yellow]"
        )
        raise typer.Exit(code=1)

    result = subprocess.run(cmd, cwd=project.root, capture_output=True, text=True)
    if result.returncode == 0:
        console.print("[dim]Rolled back: the library is not installed and not recorded.[/dim]")
    else:
        console.print(
            f"[yellow]Rollback failed. Undo it with: {' '.join(cmd)}[/yellow]"
        )
    raise typer.Exit(code=1)


def _print_measured(entry: dict, chip: str) -> None:
    measured = entry.get("measured", {}).get("targets", {})
    result = measured.get(chip.lower()) if isinstance(measured, dict) else None
    if isinstance(result, dict) and result.get("flash") is not None:
        console.print(
            f"  measured on {chip}: [bold]{result['flash']}[/bold] bytes flash, "
            f"[bold]{result.get('ram', '?')}[/bold] bytes RAM"
        )


def uninstall(
    name: str = typer.Argument(..., help="Library name or distribution to remove."),
):
    """Remove a PyMCU library from this project."""
    project = _load_project()

    libraries, _ = _installed_libraries(project)
    match = next(
        (lib for lib in libraries if name.lower() in (lib.name.lower(), lib.distribution.lower())),
        None,
    )
    distribution = match.distribution if match else name

    cmd = uninstall_command(project, distribution)
    if cmd is None:
        console.print("[red]No .venv in this project and uv is not available.[/red]")
        raise typer.Exit(code=1)

    if not _run(cmd, project.root):
        console.print(f"[red]Could not uninstall {distribution}.[/red]")
        raise typer.Exit(code=1)

    if not _uses_uv_add(project):
        _remove_dependency(project, distribution)
    console.print(f"[bold green]-[/bold green] {distribution} removed.")


def libraries(
    all_targets: bool = typer.Option(
        False, "--all", help="List every installed library, not just the usable ones."),
    json_output: bool = typer.Option(
        False, "--json", help="Emit the list as JSON on stdout (for IDE integrations)."),
):
    """List the PyMCU libraries installed in this project."""
    project = _load_project()
    installed, problems = _installed_libraries(project)

    if json_output:
        print(json.dumps({
            "chip": project.chip,
            "board": project.board,
            "flavors": project.flavors,
            "libraries": [_installed_json(lib, project) for lib in installed],
            "collisions": find_module_collisions(installed),
            "invalid": problems,
        }))
        return

    for problem in problems:
        console.print(f"[bold red]Invalid library[/bold red] {problem}")

    if not installed:
        console.print("[dim]No PyMCU libraries installed in this project.[/dim]")
        console.print("[dim]Add one with: pymcu install <name>[/dim]")
        return

    table = Table(title=f"Libraries for {project.chip or 'this project'}")
    table.add_column("Library")
    table.add_column("Version")
    table.add_column("Modules")
    table.add_column("Status")

    for lib in installed:
        reasons = check_compatibility(lib, chip=project.chip, flavors=project.flavors) \
            if project.chip else ["no board or target declared"]
        usable = not reasons
        if not usable and not all_targets:
            continue
        table.add_row(
            lib.name,
            lib.version,
            ", ".join(lib.modules),
            "[green]ok[/green]" if usable else f"[yellow]{reasons[0]}[/yellow]",
        )

    console.print(table)

    collisions = find_module_collisions(installed)
    for collision in collisions:
        console.print(f"[bold red]Collision:[/bold red] {collision}")


def search(
    query: str = typer.Argument("", help="Text to look for in names and summaries."),
    refresh: bool = typer.Option(False, "--refresh", help="Re-download the index first."),
    all_targets: bool = typer.Option(
        False, "--all", help="Include libraries that do not fit this project's chip."),
    json_output: bool = typer.Option(
        False, "--json", help="Emit the results as JSON on stdout (for IDE integrations)."),
):
    """Search the PyMCU library index."""
    index, source = fetch_index(refresh=refresh)
    if not index:
        if json_output:
            print(json.dumps({
                "error": f"Could not reach the library index at {_index_url()}.",
                "detail": last_index_error(),
            }))
            raise typer.Exit(code=1)
        console.print(f"[red]Could not reach the library index at {_index_url()}.[/red]")
        if last_index_error():
            console.print(f"[dim]{last_index_error()}[/dim]")
        raise typer.Exit(code=1)
    if source == "cache" and not json_output:
        console.print("[dim]Using the cached index (run with --refresh to update).[/dim]")

    chip, flavors = "", []
    if Path("pyproject.toml").exists():
        project = _load_project()
        chip, flavors = project.chip, project.flavors

    needle = query.strip().lower()

    if json_output:
        installed_names = set()
        if Path("pyproject.toml").exists():
            found, _ = _installed_libraries(_load_project())
            installed_names = {lib.distribution.lower() for lib in found}
        results = []
        for entry in sorted(_entries(index), key=lambda e: str(e.get("name", ""))):
            haystack = (f"{entry.get('name', '')} {entry.get('summary', '')} "
                        f"{' '.join(entry.get('categories', []))}")
            if needle and needle not in haystack.lower():
                continue
            reasons = entry_verdict(entry, chip, flavors) if chip else []
            if reasons and not all_targets:
                continue
            results.append(_entry_json(entry, reasons, installed_names))
        print(json.dumps({
            "chip": chip, "flavors": flavors, "source": source, "libraries": results,
        }))
        return
    table = Table(title="PyMCU libraries")
    table.add_column("Name")
    table.add_column("Version")
    table.add_column("Summary")
    table.add_column("Targets")

    shown = 0
    for entry in sorted(_entries(index), key=lambda e: str(e.get("name", ""))):
        haystack = f"{entry.get('name', '')} {entry.get('summary', '')} {' '.join(entry.get('categories', []))}"
        if needle and needle not in haystack.lower():
            continue
        fits = not (chip and entry_verdict(entry, chip, flavors))
        if chip and not fits and not all_targets:
            continue
        table.add_row(
            str(entry.get("name", "")),
            str(entry.get("version", "")),
            str(entry.get("summary", "")),
            ", ".join(str(a) for a in entry.get("arch", [])) or "-",
        )
        shown += 1

    if shown == 0:
        console.print("[dim]Nothing matched.[/dim]")
        if chip and not all_targets:
            console.print(f"[dim]Only libraries usable on {chip} are shown; --all lists the rest.[/dim]")
        return

    console.print(table)
    console.print("[dim]Install with: pymcu install <name>[/dim]")
