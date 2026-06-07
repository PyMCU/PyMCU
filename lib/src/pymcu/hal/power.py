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
#   sleep_extended_standby() -- power-down + async timer + fast oscillator
#
# Global interrupts must be enabled before calling sleep functions.
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError
from pymcu.types import inline

if __CHIP__.arch == "avr":
    from pymcu.hal.avr.power import (
        sleep_idle as _sleep_idle,
        sleep_adc_noise as _sleep_adc_noise,
        sleep_power_down as _sleep_power_down,
        sleep_power_save as _sleep_power_save,
        sleep_standby as _sleep_standby,
        sleep_extended_standby as _sleep_extended_standby,
    )


@inline
def sleep_idle():
    match __CHIP__.arch:
        case "avr":
            _sleep_idle()
        case _:
            raise CompileError("sleep_idle not supported on this architecture")


@inline
def sleep_adc_noise():
    match __CHIP__.arch:
        case "avr":
            _sleep_adc_noise()
        case _:
            raise CompileError("sleep_adc_noise not supported on this architecture")


@inline
def sleep_power_down():
    match __CHIP__.arch:
        case "avr":
            _sleep_power_down()
        case _:
            raise CompileError("sleep_power_down not supported on this architecture")


@inline
def sleep_power_save():
    match __CHIP__.arch:
        case "avr":
            _sleep_power_save()
        case _:
            raise CompileError("sleep_power_save not supported on this architecture")


@inline
def sleep_standby():
    match __CHIP__.arch:
        case "avr":
            _sleep_standby()
        case _:
            raise CompileError("sleep_standby not supported on this architecture")


@inline
def sleep_extended_standby():
    match __CHIP__.arch:
        case "avr":
            _sleep_extended_standby()
        case _:
            raise CompileError("sleep_extended_standby not supported on this architecture")
