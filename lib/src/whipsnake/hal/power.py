# -----------------------------------------------------------------------------
# Whipsnake Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

from whipsnake.types import uint8, inline
from whipsnake.chips import __CHIP__

# Sleep / Power Management HAL
#
# Available sleep modes, from lightest to deepest:
#   sleep_idle()          -- halts the CPU; all peripherals still running
#   sleep_adc_noise()     -- reduces digital noise for ADC conversions
#   sleep_power_down()    -- deepest sleep; wake via external interrupt, WDT, or TWI
#   sleep_power_save()    -- power-down with async timer still running (useful for RTC)
#   sleep_standby()       -- power-down with fast oscillator wake
#
# Each function enters the selected sleep mode, then clears the sleep-enable
# flag on wake. Global interrupts must be enabled before calling sleep
# functions; without a wake source the MCU will remain asleep indefinitely.
#
# Example (interrupt-driven blink):
#   from whipsnake.hal.power import sleep_power_down
#   asm("sei")
#   while True:
#       sleep_power_down()    # wakes on external interrupt
#       led.toggle()

# noinspection PyProtectedMember
@inline
def sleep_idle():
    match __CHIP__.name:
        case "atmega328p":
            from whipsnake.hal._power.atmega328p import sleep_idle as _sleep_idle
            _sleep_idle()

# noinspection PyProtectedMember
@inline
def sleep_adc_noise():
    match __CHIP__.name:
        case "atmega328p":
            from whipsnake.hal._power.atmega328p import sleep_adc_noise as _sleep_adc_noise
            _sleep_adc_noise()

# noinspection PyProtectedMember
@inline
def sleep_power_down():
    match __CHIP__.name:
        case "atmega328p":
            from whipsnake.hal._power.atmega328p import sleep_power_down as _sleep_power_down
            _sleep_power_down()


# noinspection PyProtectedMember
@inline
def sleep_power_save():
    match __CHIP__.name:
        case "atmega328p":
            from whipsnake.hal._power.atmega328p import sleep_power_save as _sleep_power_save
            _sleep_power_save()


# noinspection PyProtectedMember
@inline
def sleep_standby():
    match __CHIP__.name:
        case "atmega328p":
            from whipsnake.hal._power.atmega328p import sleep_standby as _sleep_standby
            _sleep_standby()
