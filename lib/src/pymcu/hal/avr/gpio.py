from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint16, const, inline
from pymcu.exceptions import CompileError

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
                from pymcu.hal.avr.atmega328p_gpio import _PinRegs
                _r = _PinRegs(name)
                self._port = _r._port
                self._ddr  = _r._ddr
                self._pin  = _r._pin
                self._bit  = _r._bit
            case "attiny85" | "attiny45" | "attiny25" | "attiny13" | "attiny13a":
                from pymcu.hal.avr.attiny_b_gpio import select_port, select_ddr, select_pin, select_bit
                self._port = select_port(name)
                self._ddr = select_ddr(name)
                self._pin = select_pin(name)
                self._bit = select_bit(name)
            case "attiny84" | "attiny44" | "attiny24":
                from pymcu.hal.avr.attiny_ab_gpio import select_port, select_ddr, select_pin, select_bit
                self._port = select_port(name)
                self._ddr = select_ddr(name)
                self._pin = select_pin(name)
                self._bit = select_bit(name)
            case "attiny2313" | "attiny4313":
                from pymcu.hal.avr.attiny2313_gpio import select_port, select_ddr, select_pin, select_bit
                self._port = select_port(name)
                self._ddr = select_ddr(name)
                self._pin = select_pin(name)
                self._bit = select_bit(name)
            case "atmega2560":
                from pymcu.hal.avr.atmega2560_gpio import select_port, select_ddr, select_pin, select_bit
                self._port = select_port(name)
                self._ddr = select_ddr(name)
                self._pin = select_pin(name)
                self._bit = select_bit(name)
            case "atmega32u4":
                from pymcu.hal.avr.atmega32u4_gpio import select_port, select_ddr, select_pin, select_bit
                self._port = select_port(name)
                self._ddr = select_ddr(name)
                self._pin = select_pin(name)
                self._bit = select_bit(name)

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

    @inline
    def value(self, x: const = -1) -> uint8:
        if x == -1:
            return self._pin[self._bit]
        else:
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
                from pymcu.hal.avr.atmega328p_gpio import pin_irq_setup
                pin_irq_setup(self.name, trigger, handler)
            case "atmega2560":
                from pymcu.hal.avr.atmega2560_gpio import pin_irq_setup
                pin_irq_setup(self.name, trigger, handler)
            case "atmega32u4":
                from pymcu.hal.avr.atmega32u4_gpio import pin_irq_setup
                pin_irq_setup(self.name, trigger, handler)
            case "attiny85" | "attiny45" | "attiny25" | "attiny13" | "attiny13a" | "attiny84" | "attiny44" | "attiny24" | "attiny2313" | "attiny4313":
                raise CompileError("IRQ not yet supported on ATtiny")

    @inline
    def pulse_in(self, state: uint8, timeout_us: uint16 = 1000) -> uint16:
        match __CHIP__.name:
            case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
                from pymcu.hal.avr.atmega328p_gpio import pin_pulse_in
                return pin_pulse_in(self._pin, self._bit, state, timeout_us)
            case _:
                return 0

    @inline
    def mode(self, m: const = -1) -> uint8:
        if m != -1:
            self._ddr[self._bit] = m ^ 1
