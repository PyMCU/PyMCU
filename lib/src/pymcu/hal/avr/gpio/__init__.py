# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# AVR GPIO facade -- pymcu.hal.avr.gpio
#
# Module-level conditional imports select the correct chip implementation at
# compile time. The ConditionalImportExtractor in the compiler resolves these
# if/elif chains before the dependency graph is built, so only the winning
# chip-specific module is loaded.
#
# Constant folding applies through __CHIP__.name (a string property) so each
# branch is evaluated exactly once and the dead branches are eliminated.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint16, const, inline
from pymcu.exceptions import CompileError

if __CHIP__.name == "atmega328p" or __CHIP__.name == "atmega328" or __CHIP__.name == "atmega168p" or __CHIP__.name == "atmega168" or __CHIP__.name == "atmega88p" or __CHIP__.name == "atmega88" or __CHIP__.name == "atmega48p" or __CHIP__.name == "atmega48":
    from pymcu.hal.avr.gpio.atmega328p import _PinRegs, pin_irq_setup, pin_pulse_in
elif __CHIP__.name == "attiny85" or __CHIP__.name == "attiny45" or __CHIP__.name == "attiny25" or __CHIP__.name == "attiny13" or __CHIP__.name == "attiny13a":
    from pymcu.hal.avr.gpio.attiny_b import select_port, select_ddr, select_pin, select_bit
elif __CHIP__.name == "attiny84" or __CHIP__.name == "attiny44" or __CHIP__.name == "attiny24":
    from pymcu.hal.avr.gpio.attiny_ab import select_port, select_ddr, select_pin, select_bit
elif __CHIP__.name == "attiny2313" or __CHIP__.name == "attiny4313":
    from pymcu.hal.avr.gpio.attiny2313 import select_port, select_ddr, select_pin, select_bit
elif __CHIP__.name == "atmega2560":
    # pin_irq_setup is imported here because Pin.irq() dispatches to it for this
    # chip. Without it every irq() call failed as "name 'pin_irq_setup' is not
    # defined" -- an internal helper the user never wrote -- while the chip's own
    # implementation sat in atmega2560.py, complete and unreachable through the facade.
    from pymcu.hal.avr.gpio.atmega2560 import select_port, select_ddr, select_pin, select_bit, pin_irq_setup
elif __CHIP__.name == "atmega32u4":
    # pin_irq_setup is imported here because Pin.irq() dispatches to it for this
    # chip. Without it every irq() call failed as "name 'pin_irq_setup' is not
    # defined" -- an internal helper the user never wrote -- while the chip's own
    # implementation sat in atmega32u4.py, complete and unreachable through the facade.
    from pymcu.hal.avr.gpio.atmega32u4 import select_port, select_ddr, select_pin, select_bit, pin_irq_setup


class Pin:
    IN  = 1
    OUT = 0
    OPEN_DRAIN = 2

    # Arduino spells this INPUT_PULLUP and MicroPython spells it `Pin.IN, Pin.PULL_UP`.
    # The capability was always here; only the combined name was missing, and it is the
    # second line of every button program (an internal pull-up is what lets a button work
    # without an external resistor).
    IN_PULLUP = 3

    PULL_UP   = 1
    PULL_DOWN = 2

    DRIVE_0 = 0
    DRIVE_1 = 1

    IRQ_FALLING    = 1
    IRQ_RISING     = 2
    # Any edge. pin_irq_setup has always implemented trigger 3 and irq() has always
    # defaulted to it; the capability was reachable only as `IRQ_FALLING | IRQ_RISING`,
    # which is 3 by arithmetic rather than by name.
    IRQ_CHANGE     = 3
    IRQ_LOW_LEVEL  = 4
    # Kept so the rejection can name what was asked for: the AVR external interrupts
    # encode low level, any edge, falling and rising, and nothing else. pin_irq_setup
    # raises on it.
    IRQ_HIGH_LEVEL = 8

    def __init__(self, name: str, mode: const[uint8], pull: const[uint8] = -1, value: const = -1, drive: const = 0, alt: const = -1):
        self.name = name
        if mode == 2:
            raise CompileError("Open-drain mode not supported on AVR")
        if alt != -1:
            raise CompileError("Alternate functions not supported on AVR")
        if drive:
            raise CompileError("Drive strength control not supported on AVR")

        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                _r = _PinRegs(name)
                self._port = _r._port
                self._ddr  = _r._ddr
                self._pin  = _r._pin
                self._bit  = _r._bit
            case "attiny85" | "attiny45" | "attiny25" | "attiny13" | "attiny13a":
                self._port = select_port(name)
                self._ddr = select_ddr(name)
                self._pin = select_pin(name)
                self._bit = select_bit(name)
            case "attiny84" | "attiny44" | "attiny24":
                self._port = select_port(name)
                self._ddr = select_ddr(name)
                self._pin = select_pin(name)
                self._bit = select_bit(name)
            case "attiny2313" | "attiny4313":
                self._port = select_port(name)
                self._ddr = select_ddr(name)
                self._pin = select_pin(name)
                self._bit = select_bit(name)
            case "atmega2560":
                self._port = select_port(name)
                self._ddr = select_ddr(name)
                self._pin = select_pin(name)
                self._bit = select_bit(name)
            case "atmega32u4":
                self._port = select_port(name)
                self._ddr = select_ddr(name)
                self._pin = select_pin(name)
                self._bit = select_bit(name)

        if mode == 3:
            # IN_PULLUP: input direction, pull-up on.
            self._ddr[self._bit] = 0
            self._port[self._bit] = 1
        else:
            self._ddr[self._bit] = mode ^ 1
        if pull != -1:
            if pull == 2:
                raise CompileError("Pull-down resistor not supported on AVR")
            self._port[self._bit] = pull
        if value != -1:
            self._port[self._bit] = value

    @inline
    def high(self):
        self._port[self._bit] = 1

    @inline
    def low(self):
        self._port[self._bit] = 0

    @inline
    def on(self):
        self.high()

    @inline
    def off(self):
        self.low()

    @inline
    def toggle(self):
        self._pin[self._bit] = 1

    # Two overloads instead of one const parameter: a const could only ever be a literal,
    # so `led.value(state)` with a computed state was rejected -- the canonical way to
    # drive a pin. Reading keeps its own arity, which is what the const trick was for.
    @inline
    def value(self) -> uint8:
        return self._pin[self._bit]

    @inline
    def value(self, x: uint8):
        self._port[self._bit] = x

    @inline
    def init(self, mode: const = -1, pull: const = -1, value: const = -1, drive: const = 0, alt: const = -1):
        if mode != -1:
            self._ddr[self._bit] = mode ^ 1
        if pull != -1:
            if pull == 2:
                raise CompileError("Pull-down resistor not supported on AVR")
            self._port[self._bit] = pull
        if value != -1:
            self._port[self._bit] = value
        if drive:
            raise CompileError("Drive strength control not supported on AVR")
        if alt != -1:
            raise CompileError("Alternate functions not supported on AVR")

    @inline
    def pull(self, pull_mode: const):
        if pull_mode == 2:
            raise CompileError("Pull-down resistor not supported on AVR")
        self._port[self._bit] = pull_mode

    @inline
    def drive(self, strength: uint8):
        raise CompileError("Drive strength control not supported on AVR")

    @inline
    def irq(self, trigger: const = 3, handler: const = 0):
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                pin_irq_setup(self.name, trigger, handler)
            case "atmega2560":
                pin_irq_setup(self.name, trigger, handler)
            case "atmega32u4":
                pin_irq_setup(self.name, trigger, handler)
            case "attiny85" | "attiny45" | "attiny25" | "attiny13" | "attiny13a" | "attiny84" | "attiny44" | "attiny24" | "attiny2313" | "attiny4313":
                raise CompileError("IRQ not yet supported on ATtiny")

    @inline
    def pulse_in(self, state: uint8, timeout_us: uint16 = 1000) -> uint16:
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                return pin_pulse_in(self._pin, self._bit, state, timeout_us)
            case _:
                return 0

    @inline
    def mode(self, m: const = -1) -> uint8:
        if m != -1:
            self._ddr[self._bit] = m ^ 1
