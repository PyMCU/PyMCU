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

if __CHIP__.name == "attiny85" or __CHIP__.name == "attiny45" or __CHIP__.name == "attiny25":
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
