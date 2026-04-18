# -----------------------------------------------------------------------------
# PyMCU AVR Toolchain Plugin
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# This program is free software: you can redistribute it and/or modify
# it under the terms of the GNU Affero General Public License as published
# by the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# This program is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
# GNU Affero General Public License for more details.
#
# You should have received a copy of the GNU Affero General Public License
# along with this program.  If not, see <https://www.gnu.org/licenses/>.
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

"""
AvrToolchainPlugin — PyMCU toolchain plugin for AVR targets.

Registered under the ``pymcu.toolchains`` entry-point group so the PyMCU CLI
discovers it automatically at runtime.
"""

from typing import Optional

from rich.console import Console
from pymcu_toolchain_sdk import ExternalToolchain, ToolchainPlugin

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
