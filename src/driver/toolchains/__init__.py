# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
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
    - AvrgasToolchain (AVR, avr-as + avr-ld + avr-objcopy)

    Raises:
        ValueError: If no toolchain supports the given chip.
    """
    # List of available toolchains in preference order.
    # AvrgasToolchain is preferred over AvraToolchain for AVR: avr-as ships
    # with binutils-avr (standard distro package) and does not require
    # building AVRA from source.
    toolchains = [
        GputilsToolchain,
        AvrgasToolchain,
        AvraToolchain,
    ]

    for toolchain_cls in toolchains:
        if toolchain_cls.supports(chip):
            return toolchain_cls(console, chip) if toolchain_cls is AvrgasToolchain else toolchain_cls(console)

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
