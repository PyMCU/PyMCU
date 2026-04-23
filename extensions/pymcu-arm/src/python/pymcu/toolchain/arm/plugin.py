# SPDX-License-Identifier: MIT
"""
ArmToolchainPlugin -- PyMCU toolchain plugin for ARM Cortex-M targets.

Registered under the ``pymcu.toolchains`` entry-point group so the PyMCU CLI
discovers it automatically at runtime.
"""

from rich.console import Console

from pymcu.toolchain.sdk import ExternalToolchain, ToolchainPlugin

from .llvm import LlvmArmToolchain, _TOOLCHAIN_VERSION


class ArmToolchainPlugin(ToolchainPlugin):
    """
    Toolchain plugin for the ARM Cortex-M architecture family.

    Delegates to LlvmArmToolchain (LLVM Embedded Toolchain for Arm:
    clang, llvm-objcopy, lld).
    """

    family = "arm"
    description = "ARM LLVM Embedded Toolchain (clang, llvm-objcopy)"
    version = _TOOLCHAIN_VERSION
    default_chip = "rp2040"

    @classmethod
    def supports(cls, chip: str) -> bool:
        return LlvmArmToolchain.supports(chip)

    @classmethod
    def get_toolchain(cls, console: Console, chip: str) -> ExternalToolchain:
        return LlvmArmToolchain(console, chip)
