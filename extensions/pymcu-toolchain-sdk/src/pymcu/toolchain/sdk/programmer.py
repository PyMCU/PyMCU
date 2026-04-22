# -----------------------------------------------------------------------------
# PyMCU Toolchain SDK
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

from abc import abstractmethod
from pathlib import Path
from .base_tool import CacheableTool


class HardwareProgrammer(CacheableTool):
    """
    Abstract base class for hardware programmers/debuggers (e.g., pk2cmd, picotool, avrdude).
    Inherits caching and installation logic from CacheableTool.
    """

    @abstractmethod
    def flash(self, hex_file: Path, chip: str, *, port: str | None = None, baud: int | None = None) -> None:
        """
        Flashes the firmware to the target chip.

        Args:
            hex_file: Path to the Intel HEX firmware file.
            chip: Chip identifier (e.g. "atmega328p", "pic16f84a").
            port: Serial port to use (e.g. "/dev/cu.usbmodem14101"). Optional;
                  programmers that auto-select their device may ignore it.
            baud: Baud rate for communication. Optional; defaults to programmer default.
        """
        pass
