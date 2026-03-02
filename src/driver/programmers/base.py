# -----------------------------------------------------------------------------
# PyMCU CLI Driver
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# This file is part of the PyMCU Development Ecosystem.
#
# PyMCU is free software: you can redistribute it and/or modify
# it under the terms of the GNU General Public License as published by
# the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# PyMCU is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
# GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with PyMCU.  If not, see <https://www.gnu.org/licenses/>.
#
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

from abc import ABC, abstractmethod
from pathlib import Path
from ..core.base_tool import CacheableTool

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
