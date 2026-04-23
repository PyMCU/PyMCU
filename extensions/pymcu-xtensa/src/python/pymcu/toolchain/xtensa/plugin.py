# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
# PyMCU Xtensa Plugin
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
"""
XtensaToolchainPlugin -- PyMCU toolchain plugin for Xtensa ESP targets.

Registered under the ``pymcu.toolchains`` entry-point group so the PyMCU CLI
discovers it automatically at runtime.

Toolchain auto-detection:
  1. If ``xtensa-esp-elf-clang`` is on PATH -> LLVM pipeline (ESP-IDF 5.x+).
     The LLVM path supports both GAS assembly (.asm) and LLVM IR (.ll) input.
  2. Otherwise fall back to GNU AS (``xtensa-*-elf-as``).
"""

from typing import Optional

from rich.console import Console

from pymcu.toolchain.sdk import ExternalToolchain, ToolchainPlugin

from .espgas import EspGasToolchain
from .llvm import XtensaLlvmToolchain, is_available as llvm_available


class XtensaToolchainPlugin(ToolchainPlugin):
    """
    Toolchain plugin for the Xtensa architecture family.

    Delegates to XtensaLlvmToolchain when ESP-IDF LLVM is available,
    otherwise falls back to EspGasToolchain (GNU AS).
    """

    family = "xtensa"
    description = "Xtensa toolchain (ESP-IDF LLVM or GNU AS)"
    version = "0.1.0a1"
    default_chip = "esp32"

    @classmethod
    def supports(cls, chip: str) -> bool:
        return EspGasToolchain.supports(chip)

    @classmethod
    def get_toolchain(cls, console: Console, chip: str) -> ExternalToolchain:
        if llvm_available():
            console.print(
                "[dim]Xtensa: using LLVM toolchain (xtensa-esp-elf-clang)[/dim]"
            )
            return XtensaLlvmToolchain(console, chip)
        return EspGasToolchain(console, chip)

    @classmethod
    def get_ffi_toolchain(cls, console: Console, chip: str) -> Optional[ExternalToolchain]:
        if not EspGasToolchain.supports(chip):
            return None
        if llvm_available():
            return XtensaLlvmToolchain(console, chip)
        return EspGasToolchain(console, chip)
