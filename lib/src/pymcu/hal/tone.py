# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# hal/tone.py -- square-wave tone generation (Arduino-compatible)
#
# tone(freq_hz)   -- start a continuous square wave on the tone pin
# noTone()        -- stop the square wave and silence the pin
#
# The tone pin is the hardware Timer2 OC2A output:
#   ATmega328P / ATmega168 / ATmega88 / ATmega48 : PB3  (Arduino D11)
#   ATmega2560                                    : PB4  (Arduino D10)
#   ATmega32U4                                    : PB7  (Arduino D11)
#
# The function uses Timer2 in CTC mode with the hardware toggle (COM2A0=1),
# so there is zero CPU overhead -- the timer hardware toggles the pin
# automatically with cycle-accurate timing.
#
# Conflict: tone() uses Timer2.  Do not mix with:
#   - PWM on OC2A (PB3/D11) or OC2B (PD3/D3)
#   - Any other Timer2 usage (e.g. a Timer(2) instance)
# Call noTone() before switching the pin back to GPIO or PWM.
#
# Frequency range at 16 MHz: approximately 31 Hz to 65535 Hz.
# Accuracy: within ±1 LSB of the prescaler-divided frequency.
from pymcu.chips import __CHIP__
from pymcu.types import uint16, inline


def tone(freq_hz: uint16):
    """Generate a square wave on the OC2A timer pin (D11 on Arduino Uno).

    Uses Timer2 CTC with hardware pin toggle -- zero CPU overhead.
    The wave continues until noTone() is called.

    freq_hz: desired frequency in Hz (31--65535 at 16 MHz).
    """
    match __CHIP__.name:
        case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
            from pymcu.hal._tone.atmega328p import tone_start
            tone_start(freq_hz)
        case "atmega2560":
            from pymcu.hal._tone.atmega2560 import tone_start
            tone_start(freq_hz)
        case "atmega32u4":
            from pymcu.hal._tone.atmega32u4 import tone_start
            tone_start(freq_hz)


def noTone():
    """Stop the square wave and silence the OC2A pin.

    Stops Timer2 and drives the pin low.  Safe to call even if tone()
    was never called.
    """
    match __CHIP__.name:
        case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
            from pymcu.hal._tone.atmega328p import no_tone
            no_tone()
        case "atmega2560":
            from pymcu.hal._tone.atmega2560 import no_tone
            no_tone()
        case "atmega32u4":
            from pymcu.hal._tone.atmega32u4 import no_tone
            no_tone()
