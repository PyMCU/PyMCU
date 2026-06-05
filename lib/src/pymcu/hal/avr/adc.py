# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint16, inline, Callable, const


class AnalogPin:
    """Analog input pin, zero-cost abstraction (all methods @inline)."""

    def __init__(self, channel: str):
        match __CHIP__.name:
            case "attiny85" | "attiny45" | "attiny25":
                from pymcu.hal.avr.attiny85_adc import adc_channel_admux, adc_init
                self._admux = adc_channel_admux(channel)
                adc_init(self._admux)
            case _:
                from pymcu.hal.avr.atmega328p_adc import adc_channel_admux, adc_init
                self._admux = adc_channel_admux(channel)
                adc_init(self._admux)

    @inline
    def start(self):
        match __CHIP__.name:
            case "attiny85" | "attiny45" | "attiny25":
                from pymcu.hal.avr.attiny85_adc import adc_start
                adc_start()
            case _:
                from pymcu.hal.avr.atmega328p_adc import adc_start
                adc_start()

    @inline
    def read(self) -> uint16:
        match __CHIP__.name:
            case "attiny85" | "attiny45" | "attiny25":
                from pymcu.hal.avr.attiny85_adc import adc_read
                return adc_read()
            case _:
                from pymcu.hal.avr.atmega328p_adc import adc_read
                return adc_read()

    @inline
    def start_conversion(self):
        match __CHIP__.name:
            case "attiny85" | "attiny45" | "attiny25":
                from pymcu.hal.avr.attiny85_adc import adc_start_int
                adc_start_int()
            case _:
                from pymcu.hal.avr.atmega328p_adc import adc_start_int
                adc_start_int()

    @inline
    def read_result(self) -> uint16:
        match __CHIP__.name:
            case "attiny85" | "attiny45" | "attiny25":
                from pymcu.hal.avr.attiny85_adc import adc_read_result
                return adc_read_result()
            case _:
                from pymcu.hal.avr.atmega328p_adc import adc_read_result
                return adc_read_result()

    @inline
    def irq(self, handler: Callable):
        match __CHIP__.name:
            case "attiny85" | "attiny45" | "attiny25":
                from pymcu.hal.avr.attiny85_adc import adc_irq_setup
                adc_irq_setup(handler)
            case _:
                from pymcu.hal.avr.atmega328p_adc import adc_irq_setup
                adc_irq_setup(handler)

    @inline
    def read_u16(self) -> uint16:
        match __CHIP__.name:
            case "attiny85" | "attiny45" | "attiny25":
                from pymcu.hal.avr.attiny85_adc import adc_read_u16
                return adc_read_u16()
            case _:
                from pymcu.hal.avr.atmega328p_adc import adc_read_u16
                return adc_read_u16()
