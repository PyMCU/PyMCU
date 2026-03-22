# -----------------------------------------------------------------------------
# Whipsnake Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# hal/gpio.py -- general-purpose I/O zero-cost abstraction (ZCA)
#
# Pin(name, mode) configures a digital I/O pin by name.
# All methods are @inline; chip dispatch is folded at compile time.
#
# Mode constants:   Pin.IN, Pin.OUT, Pin.OPEN_DRAIN
# Pull constants:   Pin.PULL_UP, Pin.PULL_DOWN
# Drive constants:  Pin.DRIVE_0, Pin.DRIVE_1
# IRQ triggers:     Pin.IRQ_FALLING, Pin.IRQ_RISING, Pin.IRQ_LOW_LEVEL, Pin.IRQ_HIGH_LEVEL

from whipsnake.chips import __CHIP__
from whipsnake.types import uint8, uint16, const, inline


# noinspection PyProtectedMember
class Pin:
    IN  = 1
    OUT = 0
    OPEN_DRAIN = 2

    PULL_UP   = 1
    PULL_DOWN = 2

    DRIVE_0 = 0
    DRIVE_1 = 1

    IRQ_FALLING    = 1
    IRQ_RISING     = 2
    IRQ_LOW_LEVEL  = 4
    IRQ_HIGH_LEVEL = 8

    @inline
    def __init__(self, name: str, mode: uint8, pull: const[uint8] = -1, value: const = -1, drive: const = 0, alt: const = -1):
        self.name = name
        match __CHIP__.name:
            case "pic16f18877":
                from whipsnake.hal._gpio.pic16f18877 import pin_set_mode
                pin_set_mode(name, mode)
                if pull != -1:
                    from whipsnake.hal._gpio.pic16f18877 import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(name)
                    elif pull == 0:
                        pin_pull_off(name)
                if value != -1:
                    from whipsnake.hal._gpio.pic16f18877 import pin_write
                    pin_write(name, value)
            case "pic16f877a":
                from whipsnake.hal._gpio.pic16f877a import pin_set_mode
                pin_set_mode(name, mode)
                if pull != -1:
                    from whipsnake.hal._gpio.pic16f877a import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(name)
                    elif pull == 0:
                        pin_pull_off(name)
                if value != -1:
                    from whipsnake.hal._gpio.pic16f877a import pin_write
                    pin_write(name, value)
            case "pic16f84a":
                from whipsnake.hal._gpio.pic16f84a import pin_set_mode
                pin_set_mode(name, mode)
                if pull != -1:
                    from whipsnake.hal._gpio.pic16f84a import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(name)
                    elif pull == 0:
                        pin_pull_off(name)
                if value != -1:
                    from whipsnake.hal._gpio.pic16f84a import pin_write
                    pin_write(name, value)
            case "pic10f200":
                from whipsnake.hal._gpio.pic10f200 import pin_set_mode
                pin_set_mode(name, mode)
                if pull != -1:
                    from whipsnake.hal._gpio.pic10f200 import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(name)
                    elif pull == 0:
                        pin_pull_off(name)
                if value != -1:
                    from whipsnake.hal._gpio.pic10f200 import pin_write
                    pin_write(name, value)
            case "pic18f45k50":
                from whipsnake.hal._gpio.pic18f45k50 import pin_set_mode
                pin_set_mode(name, mode)
                if pull != -1:
                    from whipsnake.hal._gpio.pic18f45k50 import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(name)
                    elif pull == 0:
                        pin_pull_off(name)
                if value != -1:
                    from whipsnake.hal._gpio.pic18f45k50 import pin_write
                    pin_write(name, value)
            case "atmega328p":
                if mode == 2:
                    raise NotImplementedError("Open-drain mode not supported on ATmega328P")
                if alt != -1:
                    raise NotImplementedError("Alternate functions not supported on ATmega328P")
                if drive != 0:
                    raise NotImplementedError("Drive strength control not supported on ATmega328P")
                from whipsnake.hal._gpio.atmega328p import select_port, select_ddr, select_pin, select_bit
                self._port = select_port(name)
                self._ddr = select_ddr(name)
                self._pin = select_pin(name)
                self._bit = select_bit(name)
                self._ddr[self._bit] = mode ^ 1
                if pull != -1:
                    if pull == 2:
                        raise NotImplementedError("Pull-down resistor not supported on ATmega328P")
                    self._port[self._bit] = pull
                if value != -1:
                    self._port[self._bit] = value

    @inline
    def high(self):
        match __CHIP__.name:
            case "pic16f18877":
                from whipsnake.hal._gpio.pic16f18877 import pin_high
                pin_high(self.name)
            case "pic16f877a":
                from whipsnake.hal._gpio.pic16f877a import pin_high
                pin_high(self.name)
            case "pic16f84a":
                from whipsnake.hal._gpio.pic16f84a import pin_high
                pin_high(self.name)
            case "pic10f200":
                from whipsnake.hal._gpio.pic10f200 import pin_high
                pin_high(self.name)
            case "pic18f45k50":
                from whipsnake.hal._gpio.pic18f45k50 import pin_high
                pin_high(self.name)
            case "atmega328p":
                self._port[self._bit] = 1

    @inline
    def low(self):
        match __CHIP__.name:
            case "pic16f18877":
                from whipsnake.hal._gpio.pic16f18877 import pin_low
                pin_low(self.name)
            case "pic16f877a":
                from whipsnake.hal._gpio.pic16f877a import pin_low
                pin_low(self.name)
            case "pic16f84a":
                from whipsnake.hal._gpio.pic16f84a import pin_low
                pin_low(self.name)
            case "pic10f200":
                from whipsnake.hal._gpio.pic10f200 import pin_low
                pin_low(self.name)
            case "pic18f45k50":
                from whipsnake.hal._gpio.pic18f45k50 import pin_low
                pin_low(self.name)
            case "atmega328p":
                self._port[self._bit] = 0

    @inline
    def on(self):
        self.high()

    @inline
    def off(self):
        self.low()

    @inline
    def toggle(self):
        match __CHIP__.name:
            case "pic16f18877":
                from whipsnake.hal._gpio.pic16f18877 import pin_toggle
                pin_toggle(self.name)
            case "pic16f877a":
                from whipsnake.hal._gpio.pic16f877a import pin_toggle
                pin_toggle(self.name)
            case "pic16f84a":
                from whipsnake.hal._gpio.pic16f84a import pin_toggle
                pin_toggle(self.name)
            case "pic10f200":
                from whipsnake.hal._gpio.pic10f200 import pin_toggle
                pin_toggle(self.name)
            case "pic18f45k50":
                from whipsnake.hal._gpio.pic18f45k50 import pin_toggle
                pin_toggle(self.name)
            case "atmega328p":
                self._port[self._bit] = self._port[self._bit] ^ 1

    @inline
    def value(self, x: const = -1) -> uint8:
        if x == -1:
            match __CHIP__.name:
                case "atmega328p":
                    return self._pin[self._bit]
                case "pic16f18877":
                    from whipsnake.hal._gpio.pic16f18877 import pin_read
                    return pin_read(self.name)
                case "pic16f877a":
                    from whipsnake.hal._gpio.pic16f877a import pin_read
                    return pin_read(self.name)
                case "pic16f84a":
                    from whipsnake.hal._gpio.pic16f84a import pin_read
                    return pin_read(self.name)
                case "pic10f200":
                    from whipsnake.hal._gpio.pic10f200 import pin_read
                    return pin_read(self.name)
                case "pic18f45k50":
                    from whipsnake.hal._gpio.pic18f45k50 import pin_read
                    return pin_read(self.name)
        else:
            match __CHIP__.name:
                case "pic16f18877":
                    from whipsnake.hal._gpio.pic16f18877 import pin_write
                    pin_write(self.name, x)
                case "pic16f877a":
                    from whipsnake.hal._gpio.pic16f877a import pin_write
                    pin_write(self.name, x)
                case "pic16f84a":
                    from whipsnake.hal._gpio.pic16f84a import pin_write
                    pin_write(self.name, x)
                case "pic10f200":
                    from whipsnake.hal._gpio.pic10f200 import pin_write
                    pin_write(self.name, x)
                case "pic18f45k50":
                    from whipsnake.hal._gpio.pic18f45k50 import pin_write
                    pin_write(self.name, x)
                case "atmega328p":
                    self._port[self._bit] = x

    @inline
    def init(self, mode: const = -1, pull: const = -1, value: const = -1, drive: const = 0, alt: const = -1):
        if mode != -1:
            match __CHIP__.name:
                case "pic16f18877":
                    from whipsnake.hal._gpio.pic16f18877 import pin_set_mode
                    pin_set_mode(self.name, mode)
                case "pic16f877a":
                    from whipsnake.hal._gpio.pic16f877a import pin_set_mode
                    pin_set_mode(self.name, mode)
                case "pic16f84a":
                    from whipsnake.hal._gpio.pic16f84a import pin_set_mode
                    pin_set_mode(self.name, mode)
                case "pic10f200":
                    from whipsnake.hal._gpio.pic10f200 import pin_set_mode
                    pin_set_mode(self.name, mode)
                case "pic18f45k50":
                    from whipsnake.hal._gpio.pic18f45k50 import pin_set_mode
                    pin_set_mode(self.name, mode)
                case "atmega328p":
                    self._ddr[self._bit] = mode ^ 1
        if pull != -1:
            match __CHIP__.name:
                case "atmega328p":
                    if pull == 2:
                        raise NotImplementedError("Pull-down resistor not supported on ATmega328P")
                    self._port[self._bit] = pull
                case "pic16f18877":
                    from whipsnake.hal._gpio.pic16f18877 import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(self.name)
                    elif pull == 0:
                        pin_pull_off(self.name)
                case "pic16f877a":
                    from whipsnake.hal._gpio.pic16f877a import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(self.name)
                    elif pull == 0:
                        pin_pull_off(self.name)
                case "pic16f84a":
                    from whipsnake.hal._gpio.pic16f84a import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(self.name)
                    elif pull == 0:
                        pin_pull_off(self.name)
                case "pic10f200":
                    from whipsnake.hal._gpio.pic10f200 import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(self.name)
                    elif pull == 0:
                        pin_pull_off(self.name)
                case "pic18f45k50":
                    from whipsnake.hal._gpio.pic18f45k50 import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(self.name)
                    elif pull == 0:
                        pin_pull_off(self.name)
        if value != -1:
            match __CHIP__.name:
                case "atmega328p":
                    self._port[self._bit] = value
                case "pic16f18877":
                    from whipsnake.hal._gpio.pic16f18877 import pin_write
                    pin_write(self.name, value)
                case "pic16f877a":
                    from whipsnake.hal._gpio.pic16f877a import pin_write
                    pin_write(self.name, value)
                case "pic16f84a":
                    from whipsnake.hal._gpio.pic16f84a import pin_write
                    pin_write(self.name, value)
                case "pic10f200":
                    from whipsnake.hal._gpio.pic10f200 import pin_write
                    pin_write(self.name, value)
                case "pic18f45k50":
                    from whipsnake.hal._gpio.pic18f45k50 import pin_write
                    pin_write(self.name, value)
        if drive != 0:
            if __CHIP__.name == "atmega328p":
                raise NotImplementedError("Drive strength control not supported on ATmega328P")
        if alt != -1:
            if __CHIP__.name == "atmega328p":
                raise NotImplementedError("Alternate functions not supported on ATmega328P")

    @inline
    def pull(self, pull_mode: const):
        match __CHIP__.name:
            case "atmega328p":
                if pull_mode == 2:
                    raise NotImplementedError("Pull-down resistor not supported on ATmega328P")
                self._port[self._bit] = pull_mode
            case "pic16f18877":
                from whipsnake.hal._gpio.pic16f18877 import pin_pull_up, pin_pull_off
                if pull_mode == 1:
                    pin_pull_up(self.name)
                elif pull_mode == 0:
                    pin_pull_off(self.name)
            case "pic16f877a":
                from whipsnake.hal._gpio.pic16f877a import pin_pull_up, pin_pull_off
                if pull_mode == 1:
                    pin_pull_up(self.name)
                elif pull_mode == 0:
                    pin_pull_off(self.name)
            case "pic16f84a":
                from whipsnake.hal._gpio.pic16f84a import pin_pull_up, pin_pull_off
                if pull_mode == 1:
                    pin_pull_up(self.name)
                elif pull_mode == 0:
                    pin_pull_off(self.name)
            case "pic10f200":
                from whipsnake.hal._gpio.pic10f200 import pin_pull_up, pin_pull_off
                if pull_mode == 1:
                    pin_pull_up(self.name)
                elif pull_mode == 0:
                    pin_pull_off(self.name)
            case "pic18f45k50":
                from whipsnake.hal._gpio.pic18f45k50 import pin_pull_up, pin_pull_off
                if pull_mode == 1:
                    pin_pull_up(self.name)
                elif pull_mode == 0:
                    pin_pull_off(self.name)

    @inline
    def drive(self, strength: uint8):
        if __CHIP__.name == "atmega328p":
            raise NotImplementedError("Drive strength control not supported on ATmega328P")

    @inline
    def irq(self, trigger: const = 3, handler: const = 0):
        # trigger: IRQ_FALLING=1, IRQ_RISING=2, IRQ_CHANGE=3, IRQ_LOW_LEVEL=4
        # handler: compile-time function reference. When provided, compile_isr()
        # inside pin_irq_setup automatically registers the function as an ISR at
        # the correct vector -- no @interrupt decorator needed on the handler.
        match __CHIP__.name:
            case "atmega328p":
                from whipsnake.hal._gpio.atmega328p import pin_irq_setup
                pin_irq_setup(self.name, trigger, handler)
            case "pic16f877a":
                from whipsnake.hal._gpio.pic16f877a import pin_irq_setup
                pin_irq_setup(self.name, trigger)
            case "pic16f84a":
                from whipsnake.hal._gpio.pic16f84a import pin_irq_setup
                pin_irq_setup(self.name, trigger)
            case "pic16f18877":
                from whipsnake.hal._gpio.pic16f18877 import pin_irq_setup
                pin_irq_setup(self.name, trigger)
            case "pic18f45k50":
                from whipsnake.hal._gpio.pic18f45k50 import pin_irq_setup
                pin_irq_setup(self.name, trigger)
            case "pic10f200":
                raise NotImplementedError("IRQ not supported on PIC10F200")

    @inline
    def pulse_in(self, state: uint8, timeout_us: uint16 = 1000) -> uint16:
        match __CHIP__.name:
            case "atmega328p":
                from whipsnake.hal._gpio.atmega328p import pin_pulse_in
                return pin_pulse_in(self._pin, self._bit, state, timeout_us)
            case _:
                return 0

    @inline
    def mode(self, m: const = -1) -> uint8:
        if m != -1:
            match __CHIP__.name:
                case "pic16f18877":
                    from whipsnake.hal._gpio.pic16f18877 import pin_set_mode
                    pin_set_mode(self.name, m)
                case "pic16f877a":
                    from whipsnake.hal._gpio.pic16f877a import pin_set_mode
                    pin_set_mode(self.name, m)
                case "pic16f84a":
                    from whipsnake.hal._gpio.pic16f84a import pin_set_mode
                    pin_set_mode(self.name, m)
                case "pic10f200":
                    from whipsnake.hal._gpio.pic10f200 import pin_set_mode
                    pin_set_mode(self.name, m)
                case "pic18f45k50":
                    from whipsnake.hal._gpio.pic18f45k50 import pin_set_mode
                    pin_set_mode(self.name, m)
                case "atmega328p":
                    self._ddr[self._bit] = m ^ 1
