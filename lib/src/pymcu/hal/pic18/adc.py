# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError
from pymcu.types import uint8, uint16, inline, Callable, const

if __CHIP__.name == "pic18f45k50":
    from pymcu.hal.pic18.pic18f45k50_adc import (
        adc_init, adc_select, adc_start, adc_busy,
        adc_read, adc_read_result, adc_read_u16,
    )
else:
    raise CompileError("ADC is not implemented for this PIC18 chip")


class AnalogPin:
    def __init__(self, channel: str):
        self.channel = channel
        adc_init(channel)

    @inline
    def start(self):
        adc_start(self.channel)

    @inline
    def read(self) -> uint16:
        return adc_read(self.channel)

    @inline
    def start_conversion(self):
        adc_start(self.channel)

    @inline
    def busy(self) -> uint8:
        return adc_busy()

    @inline
    def read_result(self) -> uint16:
        return adc_read_result()

    @inline
    def read_u16(self) -> uint16:
        return adc_read_u16(self.channel)

    @inline
    def irq(self, handler: Callable):
        raise NotImplementedError("ADC interrupts are not implemented for PIC18F45K50")
