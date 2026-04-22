# -----------------------------------------------------------------------------
# PyMCU AVR Plugin
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------

"""
AvrToolchainPlugin -- PyMCU toolchain plugin for AVR targets.

Registered under the ``pymcu.toolchains`` entry-point group so the PyMCU CLI
discovers it automatically at runtime.
"""

from typing import Optional

from rich.console import Console
from pymcu.toolchain.sdk import ExternalToolchain, ToolchainPlugin

from .avrgas import AvrgasToolchain, _TOOLCHAIN_VERSION


class AvrToolchainPlugin(ToolchainPlugin):
    """
    Toolchain plugin for the AVR architecture family.

    Delegates to AvrgasToolchain (GNU AVR binutils: avr-as, avr-gcc, avr-objcopy).
    Supports both plain assembly builds and C-interop (FFI) builds.
    """

    family = "avr"
    description = "GNU AVR binutils (avr-as, avr-gcc, avr-objcopy)"
    version = _TOOLCHAIN_VERSION
    default_chip = "atmega328p"

    @classmethod
    def supports(cls, chip: str) -> bool:
        return AvrgasToolchain.supports(chip)

    @classmethod
    def get_toolchain(cls, console: Console, chip: str) -> AvrgasToolchain:
        return AvrgasToolchain(console, chip)

    @classmethod
    def get_ffi_toolchain(cls, console: Console, chip: str) -> Optional[ExternalToolchain]:
        if not AvrgasToolchain.supports(chip):
            return None
        return AvrgasToolchain(console, chip)

