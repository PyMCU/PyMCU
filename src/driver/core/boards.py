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

# Canonical mapping from well-known board names to chip identifiers.
# Extension packages may supplement this at build time via board_chips.py.
# Both `build` and `flash` commands import from here to avoid drift.
BOARD_CHIPS: dict[str, str] = {
    "arduino_uno":   "atmega328p",
    "arduino_nano":  "atmega328p",
    "arduino_mega":  "atmega2560",
    "arduino_micro": "atmega32u4",
}


def default_programmer(chip: str) -> str:
    """Return the default programmer name for a given chip identifier."""
    return "avrdude" if chip.lower().startswith("at") else "pk2cmd"
