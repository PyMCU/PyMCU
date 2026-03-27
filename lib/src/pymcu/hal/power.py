# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

from pymcu.types import uint8, inline
from pymcu.chips import __CHIP__

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
#   from pymcu.hal.power import sleep_power_down
#   asm("sei")
#   while True:
#       sleep_power_down()    # wakes on external interrupt
#       led.toggle()

# noinspection PyProtectedMember
@inline
def sleep_idle():
    """Enter idle sleep mode.

    Halts the CPU while leaving all peripherals running. Lowest power
    saving of the available sleep modes; wake on any interrupt.
    """
    match __CHIP__.name:
        case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
            from pymcu.hal._power.atmega328p import sleep_idle as _sleep_idle
            _sleep_idle()

# noinspection PyProtectedMember
@inline
def sleep_adc_noise():
    """Enter ADC noise-reduction sleep mode.

    Stops the CPU and most digital logic to reduce switching noise during
    ADC conversions. Wake on ADC-complete interrupt or external interrupt.
    """
    match __CHIP__.name:
        case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
            from pymcu.hal._power.atmega328p import sleep_adc_noise as _sleep_adc_noise
            _sleep_adc_noise()

# noinspection PyProtectedMember
@inline
def sleep_power_down():
    """Enter power-down sleep mode (deepest sleep).

    Stops all clocks except the watchdog oscillator. Wake sources are
    limited to an external interrupt, watchdog timeout, or TWI address match.
    Global interrupts must be enabled or the MCU will not wake.
    """
    match __CHIP__.name:
        case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
            from pymcu.hal._power.atmega328p import sleep_power_down as _sleep_power_down
            _sleep_power_down()


# noinspection PyProtectedMember
@inline
def sleep_power_save():
    """Enter power-save sleep mode.

    Like power-down but keeps the asynchronous timer running, useful for
    maintaining a real-time clock during deep sleep.
    """
    match __CHIP__.name:
        case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
            from pymcu.hal._power.atmega328p import sleep_power_save as _sleep_power_save
            _sleep_power_save()


# noinspection PyProtectedMember
@inline
def sleep_standby():
    """Enter standby sleep mode.

    Like power-down but keeps the oscillator running for a faster wake-up.
    """
    match __CHIP__.name:
        case "atmega328p" | "atmega328" | "atmega168p" | "atmega168" | "atmega88p" | "atmega88" | "atmega48p" | "atmega48":
            from pymcu.hal._power.atmega328p import sleep_standby as _sleep_standby
            _sleep_standby()
