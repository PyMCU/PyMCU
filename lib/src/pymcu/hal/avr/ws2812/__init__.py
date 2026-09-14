# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# AVR WS2812 facade -- pymcu.hal.avr.ws2812
#
# One family, and the refusal names the reason rather than the chip list. The bit
# times are not a register setting that ports across an AVR: they are counted NOPs
# in a loop whose cycle budget was fixed at 16 MHz. An ATtiny85 runs the same
# instructions in a different number of nanoseconds, so the same file there would
# emit a waveform outside the WS2812 tolerances and light the wrong colours, and it
# would do it silently.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError

if __CHIP__.name == "atmega328p" or __CHIP__.name == "atmega328" or __CHIP__.name == "atmega168p" or __CHIP__.name == "atmega168" or __CHIP__.name == "atmega88p" or __CHIP__.name == "atmega88" or __CHIP__.name == "atmega48p" or __CHIP__.name == "atmega48":
    from pymcu.hal.avr.ws2812.atmega328p import ws2812_init, ws2812_write_byte, ws2812_reset
else:
    raise CompileError(
        "no WS2812 emitter for this AVR. The bit times are counted NOPs in a loop "
        "budgeted for 16 MHz on the ATmega48/88/168/328 registers, and the same "
        "instructions on another part take a different number of nanoseconds: a "
        "waveform outside the 150 ns the protocol allows lights the wrong colours "
        "without failing. Porting it means recounting the loop for that clock.")
