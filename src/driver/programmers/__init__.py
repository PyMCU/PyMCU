# -----------------------------------------------------------------------------
# PyMCU CLI Driver
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

from typing import Optional
from rich.console import Console
from importlib.metadata import entry_points
from .base import HardwareProgrammer
from .pk2cmd import Pk2cmdProgrammer
from .avrdude import AvrdudeProgrammer
from .ipecmd import IpecmdProgrammer

def get_programmer(name: str, console: Console) -> Optional[HardwareProgrammer]:
    """
    Return the programmer instance for the given name.

    Discovery order:
    1. Entry-point plugins registered under the ``pymcu.programmers`` group.
       Third-party packages register via pyproject.toml:
           [project.entry-points."pymcu.programmers"]
           my-prog = "my_package.programmer:MyProgrammer"
    2. Built-in programmers bundled with the pymcu driver (avrdude, pk2cmd, ipecmd).
    """
    eps = entry_points(group="pymcu.programmers")
    for ep in eps:
        if ep.name == name:
            cls = ep.load()
            return cls(console)

    if name == "pk2cmd":
        return Pk2cmdProgrammer(console)
    elif name == "avrdude":
        return AvrdudeProgrammer(console)
    elif name == "ipecmd":
        return IpecmdProgrammer(console)

    return None

