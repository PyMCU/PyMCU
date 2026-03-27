# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
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

from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint16, const, inline


# noinspection PyProtectedMember
class Pin:
    """Digital I/O pin, zero-cost abstraction."""

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

    def __init__(self, name: str, mode: uint8, pull: const[uint8] = -1, value: const = -1, drive: const = 0, alt: const = -1):
        """Configure a digital I/O pin.

        name:  port-pin name string (e.g. ``"PB5"``).
        mode:  ``Pin.IN``, ``Pin.OUT``, or ``Pin.OPEN_DRAIN``.
        pull:  optional pull resistor -- ``Pin.PULL_UP`` or ``Pin.PULL_DOWN``.
        value: optional initial output value (0 or 1).
        drive: optional drive-strength selector (chip-dependent).
        alt:   optional alternate-function selector (chip-dependent).
        """
        self.name = name
        match __CHIP__.name:
            case "pic16f18877":
                from pymcu.hal._gpio.pic16f18877 import pin_set_mode
                pin_set_mode(name, mode)
                if pull != -1:
                    from pymcu.hal._gpio.pic16f18877 import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(name)
                    elif pull == 0:
                        pin_pull_off(name)
                if value != -1:
                    from pymcu.hal._gpio.pic16f18877 import pin_write
                    pin_write(name, value)
            case "pic16f877a":
                from pymcu.hal._gpio.pic16f877a import pin_set_mode
                pin_set_mode(name, mode)
                if pull != -1:
                    from pymcu.hal._gpio.pic16f877a import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(name)
                    elif pull == 0:
                        pin_pull_off(name)
                if value != -1:
                    from pymcu.hal._gpio.pic16f877a import pin_write
                    pin_write(name, value)
            case "pic16f84a":
                from pymcu.hal._gpio.pic16f84a import pin_set_mode
                pin_set_mode(name, mode)
                if pull != -1:
                    from pymcu.hal._gpio.pic16f84a import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(name)
                    elif pull == 0:
                        pin_pull_off(name)
                if value != -1:
                    from pymcu.hal._gpio.pic16f84a import pin_write
                    pin_write(name, value)
            case "pic10f200":
                from pymcu.hal._gpio.pic10f200 import pin_set_mode
                pin_set_mode(name, mode)
                if pull != -1:
                    from pymcu.hal._gpio.pic10f200 import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(name)
                    elif pull == 0:
                        pin_pull_off(name)
                if value != -1:
                    from pymcu.hal._gpio.pic10f200 import pin_write
                    pin_write(name, value)
            case "pic18f45k50":
                from pymcu.hal._gpio.pic18f45k50 import pin_set_mode
                pin_set_mode(name, mode)
                if pull != -1:
                    from pymcu.hal._gpio.pic18f45k50 import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(name)
                    elif pull == 0:
                        pin_pull_off(name)
                if value != -1:
                    from pymcu.hal._gpio.pic18f45k50 import pin_write
                    pin_write(name, value)
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48" | "attiny85" | "attiny45" | "attiny25" | "attiny13" | "attiny13a" | "attiny84" | "attiny44" | "attiny24" | "attiny2313" | "attiny4313":
                if mode == 2:
                    raise NotImplementedError("Open-drain mode not supported on AVR")
                if alt != -1:
                    raise NotImplementedError("Alternate functions not supported on AVR")
                if drive != 0:
                    raise NotImplementedError("Drive strength control not supported on AVR")
                from pymcu.hal._gpio.atmega328p import select_port, select_ddr, select_pin, select_bit
                self._port = select_port(name)
                self._ddr = select_ddr(name)
                self._pin = select_pin(name)
                self._bit = select_bit(name)
                self._ddr[self._bit] = mode ^ 1
                if pull != -1:
                    if pull == 2:
                        raise NotImplementedError("Pull-down resistor not supported on AVR")
                    # noinspection PyTypeChecker
                    self._port[self._bit] = pull
                if value != -1:
                    self._port[self._bit] = value
            case "attiny85" | "attiny45" | "attiny25" | "attiny13" | "attiny13a":
                from pymcu.hal._gpio.attiny_b import select_port, select_ddr, select_pin, select_bit
                self._port = select_port(name)
                self._ddr = select_ddr(name)
                self._pin = select_pin(name)
                self._bit = select_bit(name)
                self._ddr[self._bit] = mode ^ 1
                if pull != -1:
                    if pull == 2:
                        raise NotImplementedError("Pull-down resistor not supported on ATtiny")
                    self._port[self._bit] = pull
                if value != -1:
                    self._port[self._bit] = value
            case "attiny84" | "attiny44" | "attiny24":
                from pymcu.hal._gpio.attiny_ab import select_port, select_ddr, select_pin, select_bit
                self._port = select_port(name)
                self._ddr = select_ddr(name)
                self._pin = select_pin(name)
                self._bit = select_bit(name)
                self._ddr[self._bit] = mode ^ 1
                if pull != -1:
                    if pull == 2:
                        raise NotImplementedError("Pull-down resistor not supported on ATtiny")
                    self._port[self._bit] = pull
                if value != -1:
                    self._port[self._bit] = value
            case "attiny2313" | "attiny4313":
                from pymcu.hal._gpio.attiny2313 import select_port, select_ddr, select_pin, select_bit
                self._port = select_port(name)
                self._ddr = select_ddr(name)
                self._pin = select_pin(name)
                self._bit = select_bit(name)
                self._ddr[self._bit] = mode ^ 1
                if pull != -1:
                    if pull == 2:
                        raise NotImplementedError("Pull-down resistor not supported on ATtiny")
                    self._port[self._bit] = pull
                if value != -1:
                    self._port[self._bit] = value

    @inline
    def high(self):
        """Drive the pin to the high logic level."""
        match __CHIP__.name:
            case "pic16f18877":
                from pymcu.hal._gpio.pic16f18877 import pin_high
                pin_high(self.name)
            case "pic16f877a":
                from pymcu.hal._gpio.pic16f877a import pin_high
                pin_high(self.name)
            case "pic16f84a":
                from pymcu.hal._gpio.pic16f84a import pin_high
                pin_high(self.name)
            case "pic10f200":
                from pymcu.hal._gpio.pic10f200 import pin_high
                pin_high(self.name)
            case "pic18f45k50":
                from pymcu.hal._gpio.pic18f45k50 import pin_high
                pin_high(self.name)
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48" | "attiny85" | "attiny45" | "attiny25" | "attiny13" | "attiny13a" | "attiny84" | "attiny44" | "attiny24" | "attiny2313" | "attiny4313":
                self._port[self._bit] = 1

    @inline
    def low(self):
        """Drive the pin to the low logic level."""
        match __CHIP__.name:
            case "pic16f18877":
                from pymcu.hal._gpio.pic16f18877 import pin_low
                pin_low(self.name)
            case "pic16f877a":
                from pymcu.hal._gpio.pic16f877a import pin_low
                pin_low(self.name)
            case "pic16f84a":
                from pymcu.hal._gpio.pic16f84a import pin_low
                pin_low(self.name)
            case "pic10f200":
                from pymcu.hal._gpio.pic10f200 import pin_low
                pin_low(self.name)
            case "pic18f45k50":
                from pymcu.hal._gpio.pic18f45k50 import pin_low
                pin_low(self.name)
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48" | "attiny85" | "attiny45" | "attiny25" | "attiny13" | "attiny13a" | "attiny84" | "attiny44" | "attiny24" | "attiny2313" | "attiny4313":
                self._port[self._bit] = 0

    @inline
    def on(self):
        """Drive the pin high. Alias for high()."""
        self.high()

    @inline
    def off(self):
        """Drive the pin low. Alias for low()."""
        self.low()

    @inline
    def toggle(self):
        """Toggle the pin output state."""
        match __CHIP__.name:
            case "pic16f18877":
                from pymcu.hal._gpio.pic16f18877 import pin_toggle
                pin_toggle(self.name)
            case "pic16f877a":
                from pymcu.hal._gpio.pic16f877a import pin_toggle
                pin_toggle(self.name)
            case "pic16f84a":
                from pymcu.hal._gpio.pic16f84a import pin_toggle
                pin_toggle(self.name)
            case "pic10f200":
                from pymcu.hal._gpio.pic10f200 import pin_toggle
                pin_toggle(self.name)
            case "pic18f45k50":
                from pymcu.hal._gpio.pic18f45k50 import pin_toggle
                pin_toggle(self.name)
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48" | "attiny85" | "attiny45" | "attiny25" | "attiny13" | "attiny13a" | "attiny84" | "attiny44" | "attiny24" | "attiny2313" | "attiny4313":
                self._port[self._bit] = self._port[self._bit] ^ 1

    @inline
    def value(self, x: const = -1) -> uint8:
        """Read or write the pin logical value.

        Called with no argument: returns 0 or 1 representing the current
        pin state (input or output).
        Called with ``x=0`` or ``x=1``: sets the output to that value.
        """
        if x == -1:
            match __CHIP__.name:
                case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48" | "attiny85" | "attiny45" | "attiny25" | "attiny13" | "attiny13a" | "attiny84" | "attiny44" | "attiny24" | "attiny2313" | "attiny4313":
                    return self._pin[self._bit]
                case "pic16f18877":
                    from pymcu.hal._gpio.pic16f18877 import pin_read
                    return pin_read(self.name)
                case "pic16f877a":
                    from pymcu.hal._gpio.pic16f877a import pin_read
                    return pin_read(self.name)
                case "pic16f84a":
                    from pymcu.hal._gpio.pic16f84a import pin_read
                    return pin_read(self.name)
                case "pic10f200":
                    from pymcu.hal._gpio.pic10f200 import pin_read
                    return pin_read(self.name)
                case "pic18f45k50":
                    from pymcu.hal._gpio.pic18f45k50 import pin_read
                    return pin_read(self.name)
        else:
            match __CHIP__.name:
                case "pic16f18877":
                    from pymcu.hal._gpio.pic16f18877 import pin_write
                    pin_write(self.name, x)
                case "pic16f877a":
                    from pymcu.hal._gpio.pic16f877a import pin_write
                    pin_write(self.name, x)
                case "pic16f84a":
                    from pymcu.hal._gpio.pic16f84a import pin_write
                    pin_write(self.name, x)
                case "pic10f200":
                    from pymcu.hal._gpio.pic10f200 import pin_write
                    pin_write(self.name, x)
                case "pic18f45k50":
                    from pymcu.hal._gpio.pic18f45k50 import pin_write
                    pin_write(self.name, x)
                case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48" | "attiny85" | "attiny45" | "attiny25" | "attiny13" | "attiny13a" | "attiny84" | "attiny44" | "attiny24" | "attiny2313" | "attiny4313":
                    self._port[self._bit] = x

    @inline
    def init(self, mode: const = -1, pull: const = -1, value: const = -1, drive: const = 0, alt: const = -1):
        """Reconfigure pin properties after construction.

        All parameters are optional; pass only the ones you want to change.
        Same semantics as the constructor parameters.
        """
        if mode != -1:
            match __CHIP__.name:
                case "pic16f18877":
                    from pymcu.hal._gpio.pic16f18877 import pin_set_mode
                    pin_set_mode(self.name, mode)
                case "pic16f877a":
                    from pymcu.hal._gpio.pic16f877a import pin_set_mode
                    pin_set_mode(self.name, mode)
                case "pic16f84a":
                    from pymcu.hal._gpio.pic16f84a import pin_set_mode
                    pin_set_mode(self.name, mode)
                case "pic10f200":
                    from pymcu.hal._gpio.pic10f200 import pin_set_mode
                    pin_set_mode(self.name, mode)
                case "pic18f45k50":
                    from pymcu.hal._gpio.pic18f45k50 import pin_set_mode
                    pin_set_mode(self.name, mode)
                case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48" | "attiny85" | "attiny45" | "attiny25" | "attiny13" | "attiny13a" | "attiny84" | "attiny44" | "attiny24" | "attiny2313" | "attiny4313":
                    self._ddr[self._bit] = mode ^ 1
        if pull != -1:
            match __CHIP__.name:
                case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48" | "attiny85" | "attiny45" | "attiny25" | "attiny13" | "attiny13a" | "attiny84" | "attiny44" | "attiny24" | "attiny2313" | "attiny4313":
                    if pull == 2:
                        raise NotImplementedError("Pull-down resistor not supported on ATmega328P")
                    self._port[self._bit] = pull
                case "pic16f18877":
                    from pymcu.hal._gpio.pic16f18877 import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(self.name)
                    elif pull == 0:
                        pin_pull_off(self.name)
                case "pic16f877a":
                    from pymcu.hal._gpio.pic16f877a import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(self.name)
                    elif pull == 0:
                        pin_pull_off(self.name)
                case "pic16f84a":
                    from pymcu.hal._gpio.pic16f84a import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(self.name)
                    elif pull == 0:
                        pin_pull_off(self.name)
                case "pic10f200":
                    from pymcu.hal._gpio.pic10f200 import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(self.name)
                    elif pull == 0:
                        pin_pull_off(self.name)
                case "pic18f45k50":
                    from pymcu.hal._gpio.pic18f45k50 import pin_pull_up, pin_pull_off
                    if pull == 1:
                        pin_pull_up(self.name)
                    elif pull == 0:
                        pin_pull_off(self.name)
        if value != -1:
            match __CHIP__.name:
                case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48" | "attiny85" | "attiny45" | "attiny25" | "attiny13" | "attiny13a" | "attiny84" | "attiny44" | "attiny24" | "attiny2313" | "attiny4313":
                    self._port[self._bit] = value
                case "pic16f18877":
                    from pymcu.hal._gpio.pic16f18877 import pin_write
                    pin_write(self.name, value)
                case "pic16f877a":
                    from pymcu.hal._gpio.pic16f877a import pin_write
                    pin_write(self.name, value)
                case "pic16f84a":
                    from pymcu.hal._gpio.pic16f84a import pin_write
                    pin_write(self.name, value)
                case "pic10f200":
                    from pymcu.hal._gpio.pic10f200 import pin_write
                    pin_write(self.name, value)
                case "pic18f45k50":
                    from pymcu.hal._gpio.pic18f45k50 import pin_write
                    pin_write(self.name, value)
        if drive != 0:
            if __CHIP__.arch == "avr":
                raise NotImplementedError("Drive strength control not supported on AVR")
        if alt != -1:
            if __CHIP__.arch == "avr":
                raise NotImplementedError("Alternate functions not supported on AVR")

    @inline
    def pull(self, pull_mode: const):
        """Set the internal pull resistor for this pin.

        pull_mode: ``Pin.PULL_UP``, ``Pin.PULL_DOWN``, or ``0`` to disable.
        """
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48" | "attiny85" | "attiny45" | "attiny25" | "attiny13" | "attiny13a" | "attiny84" | "attiny44" | "attiny24" | "attiny2313" | "attiny4313":
                if pull_mode == 2:
                    raise NotImplementedError("Pull-down resistor not supported on ATmega328P")
                self._port[self._bit] = pull_mode
            case "pic16f18877":
                from pymcu.hal._gpio.pic16f18877 import pin_pull_up, pin_pull_off
                if pull_mode == 1:
                    pin_pull_up(self.name)
                elif pull_mode == 0:
                    pin_pull_off(self.name)
            case "pic16f877a":
                from pymcu.hal._gpio.pic16f877a import pin_pull_up, pin_pull_off
                if pull_mode == 1:
                    pin_pull_up(self.name)
                elif pull_mode == 0:
                    pin_pull_off(self.name)
            case "pic16f84a":
                from pymcu.hal._gpio.pic16f84a import pin_pull_up, pin_pull_off
                if pull_mode == 1:
                    pin_pull_up(self.name)
                elif pull_mode == 0:
                    pin_pull_off(self.name)
            case "pic10f200":
                from pymcu.hal._gpio.pic10f200 import pin_pull_up, pin_pull_off
                if pull_mode == 1:
                    pin_pull_up(self.name)
                elif pull_mode == 0:
                    pin_pull_off(self.name)
            case "pic18f45k50":
                from pymcu.hal._gpio.pic18f45k50 import pin_pull_up, pin_pull_off
                if pull_mode == 1:
                    pin_pull_up(self.name)
                elif pull_mode == 0:
                    pin_pull_off(self.name)

    @inline
    def drive(self, strength: uint8):
        """Set the output drive strength. Support depends on the target chip."""
        if __CHIP__.arch == "avr":
            raise NotImplementedError("Drive strength control not supported on AVR")

    @inline
    def irq(self, trigger: const = 3, handler: const = 0):
        """Configure an interrupt on this pin.

        trigger: ``Pin.IRQ_FALLING``, ``Pin.IRQ_RISING``,
                 ``Pin.IRQ_LOW_LEVEL``, or ``Pin.IRQ_HIGH_LEVEL``.
        handler: compile-time function reference; when provided the
                 function is automatically registered as the ISR.
        """
        # trigger: IRQ_FALLING=1, IRQ_RISING=2, IRQ_CHANGE=3, IRQ_LOW_LEVEL=4
        # handler: compile-time function reference. When provided, compile_isr()
        # inside pin_irq_setup automatically registers the function as an ISR at
        # the correct vector -- no @interrupt decorator needed on the handler.
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                from pymcu.hal._gpio.atmega328p import pin_irq_setup
                pin_irq_setup(self.name, trigger, handler)
            case "attiny85" | "attiny45" | "attiny25" | "attiny13" | "attiny13a" | "attiny84" | "attiny44" | "attiny24" | "attiny2313" | "attiny4313":
                raise NotImplementedError("IRQ not yet supported on ATtiny")
            case "pic16f877a":
                from pymcu.hal._gpio.pic16f877a import pin_irq_setup
                pin_irq_setup(self.name, trigger)
            case "pic16f84a":
                from pymcu.hal._gpio.pic16f84a import pin_irq_setup
                pin_irq_setup(self.name, trigger)
            case "pic16f18877":
                from pymcu.hal._gpio.pic16f18877 import pin_irq_setup
                pin_irq_setup(self.name, trigger)
            case "pic18f45k50":
                from pymcu.hal._gpio.pic18f45k50 import pin_irq_setup
                pin_irq_setup(self.name, trigger)
            case "pic10f200":
                raise NotImplementedError("IRQ not supported on PIC10F200")

    @inline
    def pulse_in(self, state: uint8, timeout_us: uint16 = 1000) -> uint16:
        """Measure the duration of a pulse on this pin.

        Waits for the pin to reach ``state``, then measures how long it
        stays there. Returns the pulse width in microseconds, or 0 on timeout.
        """
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                from pymcu.hal._gpio.atmega328p import pin_pulse_in
                return pin_pulse_in(self._pin, self._bit, state, timeout_us)
            case "attiny85" | "attiny45" | "attiny25" | "attiny13" | "attiny13a" | "attiny84" | "attiny44" | "attiny24" | "attiny2313" | "attiny4313":
                return 0
            case _:
                return 0

    @inline
    def mode(self, m: const = -1) -> uint8:
        """Get or set the pin direction.

        Called with no argument: returns the current mode constant.
        Called with ``m=Pin.IN`` or ``m=Pin.OUT``: changes the direction.
        """
        if m != -1:
            match __CHIP__.name:
                case "pic16f18877":
                    from pymcu.hal._gpio.pic16f18877 import pin_set_mode
                    pin_set_mode(self.name, m)
                case "pic16f877a":
                    from pymcu.hal._gpio.pic16f877a import pin_set_mode
                    pin_set_mode(self.name, m)
                case "pic16f84a":
                    from pymcu.hal._gpio.pic16f84a import pin_set_mode
                    pin_set_mode(self.name, m)
                case "pic10f200":
                    from pymcu.hal._gpio.pic10f200 import pin_set_mode
                    pin_set_mode(self.name, m)
                case "pic18f45k50":
                    from pymcu.hal._gpio.pic18f45k50 import pin_set_mode
                    pin_set_mode(self.name, m)
                case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48" | "attiny85" | "attiny45" | "attiny25" | "attiny13" | "attiny13a" | "attiny84" | "attiny44" | "attiny24" | "attiny2313" | "attiny4313":
                    self._ddr[self._bit] = m ^ 1
