# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# AVR Power management facade -- pymcu.hal.avr.power
# ATmega328P only for now; single unconditional import.
# -----------------------------------------------------------------------------
from pymcu.types import inline
from pymcu.hal.avr.power.atmega328p import (
    sleep_idle as _sleep_idle_impl,
    sleep_adc_noise as _sleep_adc_noise_impl,
    sleep_power_down as _sleep_power_down_impl,
    sleep_power_save as _sleep_power_save_impl,
    sleep_standby as _sleep_standby_impl,
    sleep_extended_standby as _sleep_extended_standby_impl,
)


@inline
def sleep_idle():
    _sleep_idle_impl()


@inline
def sleep_adc_noise():
    _sleep_adc_noise_impl()


@inline
def sleep_power_down():
    _sleep_power_down_impl()


@inline
def sleep_power_save():
    _sleep_power_save_impl()


@inline
def sleep_standby():
    _sleep_standby_impl()


@inline
def sleep_extended_standby():
    _sleep_extended_standby_impl()
