# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
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
group.  Install a backend plugin package (e.g. ``pip install pymcu[avr]``)
to make it available.  No code in this module needs to change when new
backend packages are released.
"""

from __future__ import annotations

from importlib.metadata import entry_points
from pathlib import Path

# ---------------------------------------------------------------------------
# Install-hint table: chip prefix -> suggested pip install command
# ---------------------------------------------------------------------------
_CHIP_INSTALL_HINTS: dict[str, str] = {
    "at": "pip install pymcu[avr]",
    "avr": "pip install pymcu[avr]",
    "pic": "pip install pymcu[pic]",
    "ch32v": "pip install pymcu[riscv]",
    "riscv": "pip install pymcu[riscv]",
}


def _hint_for_chip(chip: str) -> str:
    chip_lower = chip.lower()
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


def get_backend_binary(chip: str) -> Path | None:
    """
    Return the path to the backend binary for *chip*, or None if no backend
    is installed for this chip.
    """
    plugin = get_backend_for_chip(chip)
    if plugin is None:
        return None
    return plugin.get_backend_binary()


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

    try:
        with subprocess.Popen(
            cmd,
            stdout=subprocess.PIPE,
            stderr=None,
            text=True,
            bufsize=1,
        ) as proc:
            if proc.stdout and on_output:
                for raw in proc.stdout:
                    on_output(raw.rstrip("\n").rstrip("\r"))
            elif proc.stdout:
                proc.stdout.read()
            proc.wait()

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
            f"Install the backend package: pip install pymcu[avr]"
        )
