# SPDX-License-Identifier: MIT
"""
RiscVToolchainPlugin -- PyMCU toolchain plugin for RISC-V targets.

Registered under the ``pymcu.toolchains`` entry-point group so the PyMCU CLI
discovers it automatically at runtime.
"""

from typing import Optional

from rich.console import Console

from pymcu.toolchain.sdk import ExternalToolchain, ToolchainPlugin

from .gnu import GnuRiscVToolchain, _TOOLCHAIN_VERSION


class RiscVToolchainPlugin(ToolchainPlugin):
    """
    Toolchain plugin for the RISC-V architecture family.

    Delegates to GnuRiscVToolchain (riscv-none-elf / riscv32-unknown-elf GNU binutils).
    Supports bare-metal RV32EC and RV32IMAC targets (CH32V003, CH32V103, etc.).
    """

    family = "riscv"
    description = "GNU RISC-V bare-metal toolchain (riscv-none-elf / riscv32-unknown-elf)"
    version = _TOOLCHAIN_VERSION
    default_chip = "ch32v003"

    @classmethod
    def supports(cls, chip: str) -> bool:
        return GnuRiscVToolchain.supports(chip)

    @classmethod
    def get_toolchain(cls, console: Console, chip: str) -> GnuRiscVToolchain:
        return GnuRiscVToolchain(console, chip)

    @classmethod
    def get_ffi_toolchain(cls, console: Console, chip: str) -> Optional[ExternalToolchain]:
        if not GnuRiscVToolchain.supports(chip):
            return None
        return GnuRiscVToolchain(console, chip)
