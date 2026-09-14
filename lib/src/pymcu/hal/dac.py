# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# Digital-to-analog output -- pymcu.hal.dac
#
# The mirror of pymcu.hal.adc: a pin that DRIVES a voltage instead of reading one.
# The API is the same shape on every architecture, so a compatibility layer can hand
# a 16-bit value through without knowing the part:
#
#   dac = DACPin("PA4")
#   dac.set_value_u16(32768)      # half the reference
#   dac.deinit()
#
# None of the parts PyMCU targets today has a true DAC, so every branch of the
# constructor refuses where the DACPin is written and names what does work on that
# part. That refusal is the whole point of the module: without it a program asking
# for an analog output built clean and drove nothing, which is what analogio.AnalogOut
# used to do with a warning.
#
# Adding a part with a converter is a new branch here plus a chip module with
# dac_init / dac_write_u16, and nothing above this file changes.
# -----------------------------------------------------------------------------
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError
from pymcu.types import uint16, inline, const


class DACPin:
    """One digital-to-analog output pin."""

    def __init__(self, pin: const[str] = ""):
        match __CHIP__.arch:
            case "avr":
                raise CompileError(
                    "this chip has no digital-to-analog converter. No AVR part has one, so "
                    "there is nothing to drive a steady analog voltage with. Use pymcu.hal.pwm "
                    "(PWM(pin, duty_u16=...)) and an RC low-pass filter on the pin for an "
                    "analog-like output, or drive an external converter over SPI or I2C.")
            case "pic12" | "pic14" | "pic14e":
                raise CompileError(
                    "this chip has no digital-to-analog converter this HAL can drive. Use "
                    "pymcu.hal.pwm (PWM(pin, duty_u16=...)) with an RC low-pass filter on the "
                    "pin, or drive an external converter over SPI or I2C.")
            case "pic18":
                # The PIC18F45K50 does carry a 5-bit reference DAC (DACCON0/DACCON1), but it
                # is a reference source for the comparators and is not wired out as a general
                # analog output, so this HAL does not offer it as one.
                raise CompileError(
                    "this chip has no digital-to-analog converter wired to a pin. The 5-bit "
                    "reference DAC feeds the comparators, not an output pin. Use pymcu.hal.pwm "
                    "(PWM(pin, duty_u16=...)) with an RC low-pass filter on the pin, or drive "
                    "an external converter over SPI or I2C.")
            case _:
                raise CompileError(
                    "this chip has no digital-to-analog converter. Use pymcu.hal.pwm "
                    "(PWM(pin, duty_u16=...)) with an RC low-pass filter on the pin for an "
                    "analog-like output, or drive an external converter over SPI or I2C.")

    # Drive the pin at `value` of full scale, 0 to 65535, the same unit pymcu.hal.adc reads
    # and pymcu.hal.pwm takes. Each chip resolves it to its own converter width.
    @inline
    def set_value_u16(self, value: uint16):
        pass

    @inline
    def deinit(self):
        pass
