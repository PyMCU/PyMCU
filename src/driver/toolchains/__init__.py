# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# This program is free software: you can redistribute it and/or modify
# it under the terms of the GNU Affero General Public License as published
# by the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# This program is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
# GNU Affero General Public License for more details.
#
# You should have received a copy of the GNU Affero General Public License
# along with this program.  If not, see <https://www.gnu.org/licenses/>.
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

"""
Toolchain discovery and factory functions.

Toolchains are discovered at runtime via the ``pymcu.toolchains`` entry-point
group.  Install a toolchain plugin package (e.g. ``pip install pymcu[avr]``)
to make it available.  No code in this module needs to change when new
toolchain packages are released.
"""

from __future__ import annotations

from importlib.metadata import entry_points
from typing import TYPE_CHECKING

from pymcu.toolchain.sdk import ExternalToolchain, ToolchainPlugin

if TYPE_CHECKING:
    from rich.console import Console

# ---------------------------------------------------------------------------
# Hint table: chip prefix -> suggested install command
# ---------------------------------------------------------------------------
_CHIP_INSTALL_HINTS: dict[str, str] = {
    "at": "pip install pymcu[avr]",
    "pic": "pip install pymcu[pic]",
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

def discover_plugins() -> dict[str, type[ToolchainPlugin]]:
    """
    Return all registered toolchain plugins keyed by family name.

    Plugins are discovered via the ``pymcu.toolchains`` entry-point group.
    Install a plugin package (e.g. ``pip install pymcu[avr]``) to register
    a new toolchain family.
    """
    plugins: dict[str, type[ToolchainPlugin]] = {}
    for ep in entry_points(group="pymcu.toolchains"):
        try:
            cls = ep.load()
            if isinstance(cls, type) and issubclass(cls, ToolchainPlugin):
                plugins[cls.family] = cls
        except Exception:
            pass
    return plugins


# ---------------------------------------------------------------------------
# Factory functions
# ---------------------------------------------------------------------------

def get_toolchain_for_chip(chip: str, console: "Console") -> ExternalToolchain:
    """
    Return the appropriate toolchain for *chip* by querying all registered
    toolchain plugins.

    Raises:
        ValueError: If no installed plugin supports the given chip.
    """
    for plugin_cls in discover_plugins().values():
        if plugin_cls.supports(chip):
            return plugin_cls.get_toolchain(console, chip)

    hint = _hint_for_chip(chip)
    raise ValueError(
        f"No toolchain found for chip '{chip}'.{hint}"
    )


def get_ffi_toolchain_for_chip(chip: str, console: "Console") -> ExternalToolchain:
    """
    Return an FFI-capable toolchain for *chip* (C interop via @extern).

    Raises:
        ValueError: If the chip is not supported by any installed FFI plugin.
    """
    for plugin_cls in discover_plugins().values():
        if plugin_cls.supports(chip):
            ffi_tc = plugin_cls.get_ffi_toolchain(console, chip)
            if ffi_tc is not None:
                return ffi_tc

    hint = _hint_for_chip(chip)
    raise ValueError(
        f"C interop ([tool.pymcu.ffi]) is not supported for chip '{chip}'.{hint}"
    )
