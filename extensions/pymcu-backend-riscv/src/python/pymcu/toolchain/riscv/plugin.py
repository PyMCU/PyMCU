# -----------------------------------------------------------------------------
# PyMCU RISC-V Toolchain
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
RiscvToolchainPlugin -- PyMCU toolchain plugin for WCH RISC-V targets.

Registered under the ``pymcu.toolchains`` entry-point group so the PyMCU CLI
discovers it automatically at runtime.
"""

from rich.console import Console
from pymcu.toolchain.sdk import ToolchainPlugin

from .riscvgas import RiscvGasToolchain


class RiscvToolchainPlugin(ToolchainPlugin):
    """
    Toolchain plugin for the RISC-V architecture family.

    Delegates to RiscvGasToolchain (GNU RISC-V binutils: as, ld, objcopy).
    C interop is not wired up yet, so get_ffi_toolchain keeps the base
    implementation and returns None.
    """

    family = "riscv"
    description = "GNU RISC-V binutils (as, ld, objcopy) for WCH QingKe"
    version = "0.1.0a1"
    default_chip = "ch32v003"

    @classmethod
    def supports(cls, chip: str) -> bool:
        return RiscvGasToolchain.supports(chip)

    @classmethod
    def get_toolchain(cls, console: Console, chip: str) -> RiscvGasToolchain:
        return RiscvGasToolchain(console, chip)
