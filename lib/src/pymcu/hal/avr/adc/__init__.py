# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# AVR ADC facade -- pymcu.hal.avr.adc
# Module-level conditional import selects the chip implementation.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint16, inline, Callable, const
from pymcu.exceptions import CompileError

if __CHIP__.name == "attiny2313" or __CHIP__.name == "attiny4313":
    # These parts have NO analog-to-digital converter. They used to fall through to the else
    # and compile the ATmega328P's, so a program built clean and wrote ADMUX and ADCSRA to
    # 0x7C and 0x7A, addresses that are not converter registers on this die. The ROM
    # snapshot's foreign-register check is what found it: producing a binary is not producing
    # a binary for this chip.
    raise CompileError(
        "this chip has no ADC. The ATtiny 2313 and 4313 have no analog-to-digital converter, "
        "so pymcu.hal.adc cannot read one. Use a part that has one (the ATtiny 25/45/85 and "
        "the ATmega parts do), or read the signal with an external converter over SPI or I2C.")
elif __CHIP__.name == "attiny85" or __CHIP__.name == "attiny45" or __CHIP__.name == "attiny25":
    from pymcu.hal.avr.adc.attiny85 import adc_channel_admux, adc_init, adc_select, adc_start, adc_read, adc_start_int, adc_read_result, adc_irq_setup, adc_read_u16
else:
    from pymcu.hal.avr.adc.atmega328p import adc_channel_admux, adc_init, adc_select, adc_start, adc_read, adc_start_int, adc_read_result, adc_irq_setup, adc_read_u16


class AnalogPin:
    """Analog input pin, zero-cost abstraction (all methods @inline)."""

    def __init__(self, channel: str):
        self._admux = adc_channel_admux(channel)
        adc_init(self._admux)

    @inline
    def start(self):
        adc_select(self._admux)
        adc_start()

    @inline
    def read(self) -> uint16:
        adc_select(self._admux)
        return adc_read()

    @inline
    def start_conversion(self):
        adc_select(self._admux)
        adc_start_int()

    @inline
    def read_result(self) -> uint16:
        return adc_read_result()

    @inline
    def irq(self, handler: Callable):
        adc_irq_setup(handler)

    @inline
    def read_u16(self) -> uint16:
        adc_select(self._admux)
        return adc_read_u16()
