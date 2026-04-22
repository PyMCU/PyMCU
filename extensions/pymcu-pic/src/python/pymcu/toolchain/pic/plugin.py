# -----------------------------------------------------------------------------
# PyMCU PIC Plugin
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------

"""
PicToolchainPlugin -- PyMCU toolchain plugin for PIC targets.

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

