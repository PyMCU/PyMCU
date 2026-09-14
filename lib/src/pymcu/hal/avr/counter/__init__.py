# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# AVR edge counter facade -- pymcu.hal.avr.counter
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError
from pymcu.types import uint8, uint32, inline, const

if (__CHIP__.name == "atmega328p" or __CHIP__.name == "atmega328"
        or __CHIP__.name == "atmega168p" or __CHIP__.name == "atmega168"
        or __CHIP__.name == "atmega88p" or __CHIP__.name == "atmega88"
        or __CHIP__.name == "atmega48p" or __CHIP__.name == "atmega48"):
    from pymcu.hal.avr.counter.atmega328p import (
        counter_attach, counter_read, counter_reset,
    )
else:
    # Every other AVR has its own interrupt vectors and its own pin-change map, and
    # guessing at them is how a HAL comes to write registers a die does not have.
    raise CompileError(
        "counting edges on a pin is implemented on the ATmega 48/88/168/328 family and not "
        "yet on this chip. It needs the pin interrupt vectors mapped for the part. Poll the "
        "pin with pymcu.hal.gpio and count the changes yourself, which costs the loop but "
        "needs no vector.")

from pymcu.hal.gpio import Pin as _Pin


class EdgeCounter:
    """How many edges have arrived on one pin.

    The same shape on every architecture: construct it on a pin, read the count, reset it.
    What counts the edges -- a pin interrupt, a hardware counter, a PIO program -- is the
    HAL's business.

    One per program. The counter is module state, so a second EdgeCounter would share it.
    """

    # edge: 0 both, 1 rising, 2 falling. A part that cannot tell rising from falling on this
    # pin refuses the ask rather than counting twice as many.
    def __init__(self, pin: const, edge: const[uint8] = 2, pull: const[uint8] = 1):
        # The pin is an input, with the pull-up on by default because the thing being counted
        # is usually a switch or an open-collector sensor pulling the line down.
        if pull:
            _r = _Pin(pin, _Pin.IN, _Pin.PULL_UP)
        else:
            _r = _Pin(pin, _Pin.IN)
        counter_attach(pin, edge)

    @inline
    def count(self) -> uint32:
        return counter_read()

    @inline
    def reset(self):
        counter_reset()
