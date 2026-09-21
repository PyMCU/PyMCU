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
from .avrdude import AvrdudeProgrammer
from .ipecmd import IpecmdProgrammer

# The distribution name this package installs as. An entry point registered by
# pymcu-compiler itself is a built-in, and a plugin that reuses the name is
# meant to override it -- pymcu-pic registers a pk2cmd that drives the PICkit 3
# the driver's retired implementation could not.
_OWN_DISTRIBUTION = "pymcu-compiler"

# Reached when the driver's own dist-info is not visible -- running from a
# source checkout, for instance. The PIC programmers (pk2cmd, pymcuprog) are
# not here on purpose: they ship with pymcu-pic.
_BUILTINS = {
    "avrdude": AvrdudeProgrammer,
    "ipecmd": IpecmdProgrammer,
}


def _registered_by_this_driver(ep) -> bool:
    """True when the entry point was registered by pymcu-compiler itself."""
    dist_name = getattr(getattr(ep, "dist", None), "name", "") or ""
    return dist_name.replace("_", "-").lower() == _OWN_DISTRIBUTION


def get_programmer(name: str, console: Console) -> Optional[HardwareProgrammer]:
    """
    Return the programmer instance for the given name.

    Discovery order:
    1. Entry-point plugins registered under the ``pymcu.programmers`` group.
       Third-party packages register via pyproject.toml:
           [project.entry-points."pymcu.programmers"]
           my-prog = "my_package.programmer:MyProgrammer"
       When two distributions register the same name, the plugin wins over
       this driver's own entry point -- an override is the reason a plugin
       reuses a built-in name at all, and the outcome must not depend on the
       order dist-info scanning happens to return.
    2. Built-in programmers bundled with the pymcu driver (avrdude, ipecmd).
    """
    matches = [
        ep for ep in entry_points(group="pymcu.programmers") if ep.name == name
    ]
    matches.sort(key=_registered_by_this_driver)  # plugins first; stable sort
    if matches:
        return matches[0].load()(console)

    cls = _BUILTINS.get(name)
    if cls is not None:
        return cls(console)
    return None
