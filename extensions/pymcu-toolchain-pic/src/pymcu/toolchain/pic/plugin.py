# -----------------------------------------------------------------------------
# PyMCU PIC Toolchain Plugin
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
PicToolchainPlugin — PyMCU toolchain plugin for PIC targets.

Registered under the ``pymcu.toolchains`` entry-point group so the PyMCU CLI
discovers it automatically at runtime.
"""

from rich.console import Console
from pymcu.toolchain.sdk import ToolchainPlugin

from .gputils import GputilsToolchain


class PicToolchainPlugin(ToolchainPlugin):
    """
    Toolchain plugin for the PIC architecture family.

    Delegates to GputilsToolchain (GNU PIC Utilities: gpasm/gplink).
    """

    family = "pic"
    description = "GNU PIC Utilities (gpasm/gplink)"
    version = GputilsToolchain.METADATA["version"]
    default_chip = "pic16f84a"

    @classmethod
    def supports(cls, chip: str) -> bool:
        return GputilsToolchain.supports(chip)

    @classmethod
    def get_toolchain(cls, console: Console, chip: str) -> GputilsToolchain:
        return GputilsToolchain(console, chip)
