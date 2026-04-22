# -----------------------------------------------------------------------------
# PyMCU CLI Driver
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

from typing import Optional
from rich.console import Console
from .base import HardwareProgrammer
from .pk2cmd import Pk2cmdProgrammer
from .avrdude import AvrdudeProgrammer

def get_programmer(name: str, console: Console) -> Optional[HardwareProgrammer]:
    """
    Factory method to return the appropriate programmer instance.
    Currently supports:
    - pk2cmd (PICKit 2)
    - avrdude (AVR/Arduino)
    """
    if name == "pk2cmd":
        return Pk2cmdProgrammer(console)
    elif name == "avrdude":
        return AvrdudeProgrammer(console)
    
    return None
