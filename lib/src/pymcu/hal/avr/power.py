# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
from pymcu.types import inline


@inline
def sleep_idle():
    from pymcu.hal.avr.atmega328p_power import sleep_idle as _sleep_idle
    _sleep_idle()


@inline
def sleep_adc_noise():
    from pymcu.hal.avr.atmega328p_power import sleep_adc_noise as _sleep_adc_noise
    _sleep_adc_noise()


@inline
def sleep_power_down():
    from pymcu.hal.avr.atmega328p_power import sleep_power_down as _sleep_power_down
    _sleep_power_down()


@inline
def sleep_power_save():
    from pymcu.hal.avr.atmega328p_power import sleep_power_save as _sleep_power_save
    _sleep_power_save()


@inline
def sleep_standby():
    from pymcu.hal.avr.atmega328p_power import sleep_standby as _sleep_standby
    _sleep_standby()


@inline
def sleep_extended_standby():
    from pymcu.hal.avr.atmega328p_power import sleep_extended_standby as _sleep_ext_standby
    _sleep_ext_standby()
