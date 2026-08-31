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

"""
Backend discovery and factory functions.

Backends are discovered at runtime via the ``pymcu.backends`` entry-point
group.  Install a backend plugin package (e.g. ``pip install "pymcu-compiler[avr]"``)
to make it available.  No code in this module needs to change when new
backend packages are released.
"""

from __future__ import annotations

from importlib.metadata import entry_points
import os
from pathlib import Path

# ---------------------------------------------------------------------------
# Install-hint table: chip prefix -> suggested pip install command
#
# The extras here must exist in pyproject.toml and the distribution is
# pymcu-compiler, not pymcu: this text is meant to be pasted into a shell, so
# a name that resolves to nothing sends the reader further from a working
# install. Quoted because brackets are glob characters in zsh. Escaped for
# rich, which otherwise reads "[avr]" as a style tag and drops it silently --
# leaving advice that installs the compiler without the backend it is
# complaining about.
# ---------------------------------------------------------------------------
_CHIP_INSTALL_HINTS: dict[str, str] = {
    "at": 'pip install "pymcu-compiler\\[avr]"',
    "avr": 'pip install "pymcu-compiler\\[avr]"',
    "pic": 'pip install "pymcu-compiler\\[pic]"',
    "rp2040": 'pip install "pymcu-compiler\\[arm]"',
    "rp2350": 'pip install "pymcu-compiler\\[arm]"',
}

# No RISC-V entry: pymcu-backend-riscv is not published yet, so there is no
# install command to give. Saying so beats naming an extra that cannot resolve.
_UNRELEASED_HINT = " The RISC-V backend is not released yet."
_UNRELEASED_PREFIXES = ("ch32v", "riscv")


def _hint_for_chip(chip: str) -> str:
    chip_lower = chip.lower()
    if chip_lower.startswith(_UNRELEASED_PREFIXES):
        return _UNRELEASED_HINT
    for prefix, hint in _CHIP_INSTALL_HINTS.items():
        if chip_lower.startswith(prefix):
            return f" Try: {hint}"
    return ""


# ---------------------------------------------------------------------------
# Plugin discovery
# ---------------------------------------------------------------------------

def discover_backends() -> dict[str, type]:
    """
    Return all registered backend plugins keyed by family name.

    Plugins are discovered via the ``pymcu.backends`` entry-point group.
    Returns an empty dict if no backend packages are installed.
    """
    try:
        from pymcu.backend.sdk import BackendPlugin
    except ImportError:
        return {}

    plugins: dict[str, type] = {}
    for ep in entry_points(group="pymcu.backends"):
        try:
            cls = ep.load()
            if isinstance(cls, type) and issubclass(cls, BackendPlugin):
                plugins[cls.family] = cls
        except Exception:
            pass
    return plugins


# ---------------------------------------------------------------------------
# Lookup helpers
# ---------------------------------------------------------------------------

def get_backend_for_chip(chip: str) -> type | None:
    """
    Return the BackendPlugin class that handles *chip*, or None if not found.

    Does NOT raise — callers decide whether to fall back or abort.
    """
    for plugin_cls in discover_backends().values():
        if plugin_cls.supports(chip):
            return plugin_cls
    return None


def require_backend_for_chip(chip: str) -> type:
    """
    Return the BackendPlugin class for *chip* or raise ValueError with a
    helpful install hint if no backend is found.

    Raises:
        ValueError: If no installed backend plugin supports the given chip.
    """
    plugin = get_backend_for_chip(chip)
    if plugin is not None:
        return plugin
    hint = _hint_for_chip(chip)
    raise ValueError(
        f"No codegen backend found for chip '{chip}'.{hint}"
    )


def _binary_override(family: str) -> Path | None:
    """PYMCU_BACKEND_BINARY, as `avr=/path/to/pymcuc-avr[,pic=/other]`.

    A tool that has to measure a compiler which is NOT the installed one -- the ROM
    snapshot comparing two builds, a bisection, a mutation run -- could until now only
    aim the whole stack with PYTHONPATH. That works and fails silently: a wrong path is
    not an error, it measures the deployed compiler and reports green. Two runs were lost
    to exactly that. Naming one binary fails loudly instead.

    A path that does not exist raises here rather than falling back, for the same reason:
    the point of the variable is to be sure which compiler ran.
    """
    raw = os.environ.get("PYMCU_BACKEND_BINARY")
    if not raw:
        return None
    for item in raw.split(","):
        item = item.strip()
        if not item:
            continue
        name, _, path = item.partition("=")
        if not path:
            raise ValueError(
                "PYMCU_BACKEND_BINARY must be 'family=/path' (for example "
                f"'avr=/tmp/pymcuc-avr'), got {item!r}")
        if name.strip() != family:
            continue
        resolved = Path(path.strip()).expanduser()
        if not resolved.exists():
            raise FileNotFoundError(
                f"PYMCU_BACKEND_BINARY names '{resolved}' for '{family}' and it does not "
                "exist. Refusing to fall back to the installed backend: the whole point of "
                "this variable is knowing which compiler produced the output.")
        return resolved
    return None


def get_backend_binary(chip: str) -> Path | None:
    """
    Return the path to the backend binary for *chip*, or None if no backend
    is installed for this chip.
    """
    plugin = get_backend_for_chip(chip)
    if plugin is None:
        return None
    return binary_for_plugin(plugin)


def binary_for_plugin(plugin) -> Path | None:
    """The binary a plugin should be run as, honouring PYMCU_BACKEND_BINARY.

    Every caller that spawns a backend goes through here, so there is one answer to
    "which compiler ran" rather than one per call site.
    """
    override = _binary_override(getattr(plugin, "family", ""))
    return override if override is not None else plugin.get_backend_binary()


def run_backend(
    backend_binary: Path,
    ir_file: Path,
    output_file: Path,
    target: str,
    freq: int,
    configs: dict,
    reset_vector: int | None = None,
    interrupt_vector: int | None = None,
    verbose: bool = False,
    on_output=None,
    emit_symbols_path: Path | None = None,
    emit_linemap_path: Path | None = None,
    emit_varmap_path: Path | None = None,
) -> None:
    """
    Invoke an external backend binary (e.g. pymcuc-avr) to translate a .mir
    IR file into an assembler output file.

    The backend binary must speak the pymcuc-avr CLI protocol:
      <binary> <ir-file> --output <asm-file> --target <chip> --freq <hz>
                          [--config KEY=VALUE]... [--reset-vector N]
                          [--interrupt-vector N]

    Raises:
        RuntimeError: If the backend exits with a non-zero status code.
    """
    import subprocess
    import time

    cmd = [
        str(backend_binary),
        str(ir_file),
        "--output", str(output_file),
        "--target", target,
        "--freq", str(freq),
    ]
    if reset_vector is not None:
        cmd.extend(["--reset-vector", str(reset_vector)])
    if interrupt_vector is not None:
        cmd.extend(["--interrupt-vector", str(interrupt_vector)])
    for key, val in configs.items():
        cmd.extend(["--config", f"{key}={val}"])
    if verbose:
        cmd.append("--verbose")
    if emit_symbols_path is not None:
        cmd.extend(["--emit-symbols", str(emit_symbols_path)])
    if emit_linemap_path is not None:
        cmd.extend(["--emit-linemap", str(emit_linemap_path)])
    if emit_varmap_path is not None:
        cmd.extend(["--emit-varmap", str(emit_varmap_path)])

    # returncode == -9 means the backend was SIGKILL'd by the OS -- on macOS the kernel
    # reclaims processes under load (jetsam) when many builds run in parallel. That is
    # never a legitimate compiler result and is transient, so retry a few times before
    # surfacing it. This only happens on POSIX; on Windows negative return codes do not
    # map to signals, so the retry is simply inert there. We deliberately do NOT retry
    # other signals: a crash (SIGSEGV/SIGABRT) is a deterministic backend bug that should
    # fail fast, and real codegen errors exit with a positive code (1, 2) reported on the
    # first attempt.
    SIGKILL_RETURNCODE = -9
    max_signal_retries = 3
    try:
        for attempt in range(max_signal_retries + 1):
            buffered: list[str] = []
            # encoding pinned to utf-8: backends emit utf-8, and Popen(text=True) would
            # otherwise decode with the locale codepage (cp1252 on Windows).
            with subprocess.Popen(
                cmd,
                stdout=subprocess.PIPE,
                stderr=None,
                text=True,
                encoding="utf-8",
                errors="replace",
                bufsize=1,
            ) as proc:
                if proc.stdout:
                    buffered = [raw.rstrip("\r\n") for raw in proc.stdout]
                proc.wait()

            if proc.returncode == SIGKILL_RETURNCODE and attempt < max_signal_retries:
                time.sleep(0.25 * (attempt + 1))
                continue
            break

        if on_output:
            for line in buffered:
                on_output(line)

        if proc.returncode == 2:
            raise RuntimeError(
                f"Backend license error (exit code 2). "
                f"Run 'pymcu backend check' for details, or set PYMCU_LICENSE_KEY."
            )
        if proc.returncode != 0:
            raise RuntimeError(
                f"Backend codegen failed (exit code {proc.returncode}). "
                "See diagnostics above."
            )
    except FileNotFoundError:
        raise RuntimeError(
            f"Backend binary not found: {backend_binary}\n"
            f"Install the backend package.{_hint_for_chip(target)}"
        )
