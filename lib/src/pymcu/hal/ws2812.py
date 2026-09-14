# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# WS2812 one-wire pixels -- pymcu.hal.ws2812
#
#   ws2812_init("PD6")            # the data pin, driven low
#   ws2812_write_byte("PD6", g)   # one byte, MSB first, on the wire
#   ws2812_write_byte("PD6", r)
#   ws2812_write_byte("PD6", b)
#   ws2812_reset("PD6")           # hold low past 50 us so the strip latches
#
# Bytes go out in the strip's own order, which for a WS2812 is green, red, blue.
# This layer does not reorder them: what a caller has is a buffer, and rearranging
# it here would put a second opinion between the caller and the wire.
#
# There is no clock line, so the bit times ARE the protocol, and how they are made
# is the chip's business: counted NOPs on an AVR, a PIO program on an RP2040, a DMA
# to a timer's compare unit elsewhere. Which of those is used never reaches a
# caller. What does reach a caller is that interrupts have to be off across a
# frame, because an interrupt taken mid-byte stretches one high time past its
# tolerance and the strip latches the wrong colour.
# -----------------------------------------------------------------------------
# Thin @inline wrappers rather than a bare re-export, which is the shape hal/tone.py
# uses and the one that works: a conditional import brings a name in for this module
# to call, and a caller importing it straight back out is refused as "not exported by
# pymcu.hal.ws2812". The wrapper is the export.
#
# `pin` is `str` rather than `const`, which is not the safer spelling and is the one
# that works. Every arm is chosen by a `match` on the name, and a match only answers
# correctly for a value known at compile time, so `const` is what says so. But the
# driver above holds its pin in a ZCA field, and a field read through one more
# @inline hop does not satisfy the const check (PyMCU#253) even where the fold does
# happen: declaring it refused `NeoPixel("PD6", 1).show()`, which has worked for as
# long as the driver has existed.
#
# So the fold is measured instead of declared. tests/integration/fixtures/
# compat-cp-neopixel-write asserts the emitted code touches one port and one bit and
# times the pulses, which is what a pin that silently folded to the wrong arm would
# fail. Tightening this to `const` waits on #253.
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError
from pymcu.types import uint8, inline

if __CHIP__.arch == "avr":
    from pymcu.hal.avr.ws2812 import ws2812_init as _init
    from pymcu.hal.avr.ws2812 import ws2812_write_byte as _write_byte
    from pymcu.hal.avr.ws2812 import ws2812_reset as _reset


@inline
def ws2812_init(pin: str):
    """Drive the data pin low and leave it there, ready for a frame."""
    match __CHIP__.arch:
        case "avr":
            _init(pin)
        case _:
            raise CompileError(
                "WS2812 pixels are not implemented on this architecture yet. The "
                "protocol has no clock line, so driving it means holding a pin high "
                "for 375 ns and for 812 ns to within 150 ns, and nothing has been "
                "counted for this part. On a chip with PIO, that is what PIO is for.")


@inline
def ws2812_write_byte(pin: str, val: uint8):
    """One byte onto the wire, most significant bit first."""
    match __CHIP__.arch:
        case "avr":
            _write_byte(pin, val)
        case _:
            raise CompileError(
                "WS2812 pixels are not implemented on this architecture yet. The "
                "protocol has no clock line, so driving it means holding a pin high "
                "for 375 ns and for 812 ns to within 150 ns, and nothing has been "
                "counted for this part. On a chip with PIO, that is what PIO is for.")


@inline
def ws2812_reset(pin: str):
    """Hold the line low past 50 us so the strip latches what it was sent."""
    match __CHIP__.arch:
        case "avr":
            _reset(pin)
        case _:
            raise CompileError(
                "WS2812 pixels are not implemented on this architecture yet. The "
                "protocol has no clock line, so driving it means holding a pin high "
                "for 375 ns and for 812 ns to within 150 ns, and nothing has been "
                "counted for this part. On a chip with PIO, that is what PIO is for.")
