# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# hal/power.py -- sleep / power management
#
# Available sleep modes (lightest to deepest):
#   sleep_idle()          -- halts CPU; all peripherals still running
#   sleep_adc_noise()     -- reduces digital noise for ADC conversions
#   sleep_power_down()    -- deepest sleep; wake via ext interrupt, WDT, or TWI
#   sleep_power_save()    -- power-down with async timer still running
#   sleep_standby()       -- power-down with fast oscillator wake
#
# Global interrupts must be enabled before calling sleep functions.
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError
from pymcu.types import uint8, inline


@inline
def sleep_idle():
    match __CHIP__.arch:
        case "avr":
            from pymcu.hal.avr.atmega328p_power import sleep_idle as _impl
            _impl()
        case _:
            raise CompileError("sleep_idle not supported on this architecture")


@inline
def sleep_adc_noise():
    match __CHIP__.arch:
        case "avr":
            from pymcu.hal.avr.atmega328p_power import sleep_adc_noise as _impl
            _impl()
        case _:
            raise CompileError("sleep_adc_noise not supported on this architecture")


@inline
def sleep_power_down():
    match __CHIP__.arch:
        case "avr":
            from pymcu.hal.avr.atmega328p_power import sleep_power_down as _impl
            _impl()
        case _:
            raise CompileError("sleep_power_down not supported on this architecture")


@inline
def sleep_power_save():
    match __CHIP__.arch:
        case "avr":
            from pymcu.hal.avr.atmega328p_power import sleep_power_save as _impl
            _impl()
        case _:
            raise CompileError("sleep_power_save not supported on this architecture")


@inline
def sleep_standby():
    match __CHIP__.arch:
        case "avr":
            from pymcu.hal.avr.atmega328p_power import sleep_standby as _impl
            _impl()
        case _:
            raise CompileError("sleep_standby not supported on this architecture")


@inline
def sleep_extended_standby():
    match __CHIP__.arch:
        case "avr":
            from pymcu.hal.avr.atmega328p_power import sleep_extended_standby as _impl
            _impl()
        case _:
            raise CompileError("sleep_extended_standby not supported on this architecture")
