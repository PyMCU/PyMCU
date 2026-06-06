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

# Canonical mapping from well-known board names to chip identifiers.
# Extension packages may supplement this at build time via board_chips.py.
# Both `build` and `flash` commands import from here to avoid drift.
BOARD_CHIPS: dict[str, str] = {
    # Arduino AVR boards
    "arduino_uno":   "atmega328p",
    "arduino_nano":  "atmega328p",
    "arduino_mega":  "atmega2560",
    "arduino_micro": "atmega32u4",
    # ATtiny named dev boards
    "digispark":          "attiny85",
    "adafruit_trinket":   "attiny85",
    # ATtiny bare chips -- 8-pin (PB0-PB5)
    "attiny85":  "attiny85",
    "attiny45":  "attiny45",
    "attiny25":  "attiny25",
    "attiny13":  "attiny13",
    "attiny13a": "attiny13a",
    # ATtiny bare chips -- 14-pin (PA0-PA7 + PB0-PB2)
    "attiny84":  "attiny84",
    "attiny44":  "attiny44",
    "attiny24":  "attiny24",
    # ATtiny bare chips -- 20-pin (PD0-PD6 + PB0-PB7)
    "attiny2313":  "attiny2313",
    "attiny4313":  "attiny4313",
    # RP2040 (ARM Cortex-M0+) boards
    "raspberry_pi_pico": "rp2040",
    "pico":              "rp2040",
    "rp2040":            "rp2040",
}


def default_programmer(chip: str) -> str:
    """Return the default programmer name for a given chip identifier."""
    return "avrdude" if chip.lower().startswith("at") else "pk2cmd"
