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
    # RP2350 (ARM Cortex-M33) boards
    "raspberry_pi_pico2": "rp2350",
    "pico2":              "rp2350",
    "rp2350":             "rp2350",
}


# Default CPU frequencies by board name.  Boards not listed here fall back to
# default_frequency(chip) -- 8 MHz for the ATtinys running off their internal RC
# oscillator, 125/150 MHz for the RP2040/RP2350.  The digispark/trinket ship a
# 16.5 MHz crystal used by V-USB, so they get their own entry.
BOARD_FREQUENCIES: dict[str, int] = {
    "arduino_uno":      16_000_000,
    "arduino_nano":     16_000_000,
    "arduino_mega":     16_000_000,
    "arduino_micro":    16_000_000,
    "digispark":        16_500_000,
    "adafruit_trinket": 16_500_000,
}


BOARD_GROUPS: dict[str, list[str]] = {
    "Arduino": ["arduino_uno", "arduino_nano", "arduino_mega", "arduino_micro"],
    "Raspberry Pi": ["raspberry_pi_pico", "raspberry_pi_pico2"],
    "Adafruit": ["adafruit_trinket"],
    "Digispark": ["digispark"],
    "ATtiny 8-pin (bare chip)":  ["attiny85",  "attiny45",  "attiny25",  "attiny13", "attiny13a"],
    "ATtiny 14-pin (bare chip)": ["attiny84",  "attiny44",  "attiny24"],
    "ATtiny 20-pin (bare chip)": ["attiny2313", "attiny4313"],
}


def default_toolchain(chip: str) -> str:
    """Return the toolchain name for a given chip without requiring plugins installed."""
    chip_lower = chip.lower()
    if chip_lower.startswith("at"):
        return "avr"
    if chip_lower in ("rp2040", "rp2350"):
        return "rp2040"
    if chip_lower.startswith("pic"):
        return "gputils"
    if chip_lower.startswith("ch32v"):
        return "riscv"
    return "avr"


def default_programmer(chip: str) -> str:
    """Return the default programmer name for a given chip identifier."""
    chip_lower = chip.lower()
    if chip_lower.startswith("at"):
        return "avrdude"
    if chip_lower in ("rp2040", "rp2350"):
        return "rp2040"
    if chip_lower.startswith("ch32v"):
        return "wch-link"
    return "pk2cmd"


def default_frequency(chip: str) -> int:
    """Return the clock a chip runs at by default, in Hz.

    Used to scaffold [tool.pymcu].frequency for boards without an explicit
    BOARD_FREQUENCIES entry.  The RP values match the clk_sys the RP HAL
    assumes (see lib/src/pymcu/hal/rp2040/pwm.py and rp2350/clocks.py).
    """
    chip_lower = chip.lower()
    if chip_lower == "rp2040":
        return 125_000_000
    if chip_lower == "rp2350":
        return 150_000_000
    if chip_lower.startswith("pic"):
        return 4_000_000
    if chip_lower.startswith("ch32v2") or chip_lower.startswith("ch32v3"):
        # HSI 8 MHz through the PLL up to the V203's maximum.
        return 144_000_000
    if chip_lower.startswith("ch32v"):
        # HSI 24 MHz through the PLL, which is what the chip file assumes.
        return 48_000_000
    return 8_000_000


def board_frequency(board: str) -> int:
    """Return the scaffold frequency for a board name, in Hz."""
    if board in BOARD_FREQUENCIES:
        return BOARD_FREQUENCIES[board]
    return default_frequency(BOARD_CHIPS.get(board, ""))


def firmware_artifacts(chip: str) -> tuple[str, ...]:
    """Return the dist/ firmware filenames a chip can be flashed from.

    Most preferred first.  AVR and PIC ship Intel HEX; the RP targets ship a
    flat flash image (and a .uf2 when the toolchain produced one).
    """
    chip_lower = chip.lower()
    if chip_lower in ("rp2040", "rp2350"):
        return ("firmware.uf2", "firmware.bin")
    if chip_lower.startswith("ch32v"):
        return ("firmware.bin", "firmware.hex")
    return ("firmware.hex",)
