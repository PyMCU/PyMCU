# SPDX-License-Identifier: MIT
"""
PIOToolchainPlugin -- PyMCU toolchain plugin for RP2040 PIO state-machine programs.

Registered under the ``pymcu.toolchains`` entry-point group so the PyMCU CLI
discovers it automatically at runtime.
"""

from typing import Optional

from rich.console import Console

from pymcu.toolchain.sdk import ExternalToolchain, ToolchainPlugin

from .pioasm import PioasmToolchain, _TOOLCHAIN_VERSION


class PIOToolchainPlugin(ToolchainPlugin):
    """
    Toolchain plugin for the RP2040 PIO architecture.

    Delegates to PioasmToolchain (pioasm from the Raspberry Pi Pico SDK).
    """

    family = "pio"
    description = "pioasm assembler (Raspberry Pi Pico SDK)"
    version = _TOOLCHAIN_VERSION
    default_chip = "rp2040"

    @classmethod
    def supports(cls, chip: str) -> bool:
        return PioasmToolchain.supports(chip)

    @classmethod
    def get_toolchain(cls, console: Console, chip: str) -> PioasmToolchain:
        return PioasmToolchain(console, chip)

    @classmethod
    def get_ffi_toolchain(cls, console: Console, chip: str) -> Optional[ExternalToolchain]:
        return None
