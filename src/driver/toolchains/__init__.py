# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# This file is part of the PyMCU Development Ecosystem.
#
# PyMCU is free software: you can redistribute it and/or modify
# it under the terms of the GNU General Public License as published by
# the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# PyMCU is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
# GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with PyMCU.  If not, see <https://www.gnu.org/licenses/>.
#
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

from rich.console import Console
from .base import ExternalToolchain
from .gputils import GputilsToolchain
from .avra import AvraToolchain
from .avrgas import AvrgasToolchain

def get_toolchain_for_chip(chip: str, console: Console) -> ExternalToolchain:
    """
    Factory method to return the appropriate toolchain for a given chip.
    Currently supports:
    - Gputils (PIC10/12/14/16/17/18)
    - Avra (AVR)

    Raises:
        ValueError: If no toolchain supports the given chip.
    """
    # List of available toolchains
    toolchains = [
        GputilsToolchain,
        AvraToolchain
    ]

    for toolchain_cls in toolchains:
        if toolchain_cls.supports(chip):
            return toolchain_cls(console)

    raise ValueError(f"No toolchain found supporting chip: {chip}")


def get_ffi_toolchain_for_chip(chip: str, console: Console) -> AvrgasToolchain:
    """
    Return an AvrgasToolchain (avr-as + avr-ld + avr-objcopy) for chips that
    support C interop via @extern().  Currently all AVR chips are supported.

    Raises:
        ValueError: If the chip is not supported by AvrgasToolchain.
    """
    if not AvrgasToolchain.supports(chip):
        raise ValueError(
            f"C interop ([tool.pymcu.ffi]) is not supported for chip: {chip}"
        )
    return AvrgasToolchain(console, chip)
