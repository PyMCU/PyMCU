# -----------------------------------------------------------------------------
# PyMCU RP2040 Toolchain Plugin
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
Rp2040ToolchainPlugin -- PyMCU toolchain plugin for RP2040 targets.

Registered under the ``pymcu.toolchains`` entry-point group so the PyMCU CLI
discovers it automatically. Delegates to Rp2040LlvmToolchain (opt / llc /
llvm-mc / ld.lld / llvm-objcopy).
"""

from typing import Optional

from rich.console import Console
from pymcu.toolchain.sdk import ExternalToolchain, ToolchainPlugin

from .llvm import Rp2040LlvmToolchain


class Rp2040ToolchainPlugin(ToolchainPlugin):
    family = "rp2040"
    description = "LLVM toolchain (opt, llc, llvm-mc, ld.lld, llvm-objcopy)"
    version = "0.1.0a1"
    default_chip = "rp2040"

    @classmethod
    def supports(cls, chip: str) -> bool:
        return Rp2040LlvmToolchain.supports(chip)

    @classmethod
    def get_toolchain(cls, console: Console, chip: str) -> Rp2040LlvmToolchain:
        return Rp2040LlvmToolchain(console, chip)

    @classmethod
    def get_ffi_toolchain(cls, console: Console, chip: str) -> Optional[ExternalToolchain]:
        # C interop (FFI) is not wired for RP2040 yet.
        return None
