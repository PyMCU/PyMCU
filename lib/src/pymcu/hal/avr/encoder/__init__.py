# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# AVR quadrature facade -- pymcu.hal.avr.encoder
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError
from pymcu.types import int32, inline, const

if (__CHIP__.name == "atmega328p" or __CHIP__.name == "atmega328"
        or __CHIP__.name == "atmega168p" or __CHIP__.name == "atmega168"
        or __CHIP__.name == "atmega88p" or __CHIP__.name == "atmega88"
        or __CHIP__.name == "atmega48p" or __CHIP__.name == "atmega48"):
    from pymcu.hal.avr.encoder.atmega328p import (
        encoder_attach, encoder_read, encoder_write,
    )
else:
    # Every other AVR has its own interrupt vectors and its own pin-change map, and guessing
    # at them is how a HAL comes to write registers a die does not have.
    raise CompileError(
        "reading a quadrature encoder is implemented on the ATmega 48/88/168/328 family and "
        "not yet on this chip. It needs the pin interrupt vectors mapped for the part. Read "
        "the two pins in your loop with pymcu.hal.gpio and decode them yourself, which costs "
        "the loop but needs no vector.")

from pymcu.hal.gpio import Pin as _Pin


class Quadrature:
    """Where a two-track knob has turned to.

    The same shape on every architecture: construct it on two pins, read the count, write it.
    One detent of a common knob is four counts, because both lines change twice per detent;
    dividing is the caller's business, because the number of counts per detent is a property
    of the knob and not of the chip.

    One per program. The position is module state, so a second Quadrature would share it.
    """

    def __init__(self, pin_a: const, pin_b: const, pull: const[int32] = 1):
        # Both lines are inputs, with the pull-ups on by default: the common knob is two
        # switches to ground and reads as all ones with nothing touching it.
        if pull:
            _ra = _Pin(pin_a, _Pin.IN, _Pin.PULL_UP)
            _rb = _Pin(pin_b, _Pin.IN, _Pin.PULL_UP)
        else:
            _ra = _Pin(pin_a, _Pin.IN)
            _rb = _Pin(pin_b, _Pin.IN)
        encoder_attach(pin_a, pin_b)

    @inline
    def position(self) -> int32:
        return encoder_read()

    @inline
    def set_position(self, value: int32):
        encoder_write(value)
