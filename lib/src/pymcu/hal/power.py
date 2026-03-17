# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# This file is part of the PyMCU Standard Library (pymcu-stdlib).
# Licensed under the GNU General Public License v3 or later.
# See uart.py for full license notice.
# -----------------------------------------------------------------------------

from pymcu.types import uint8, inline
from pymcu.chips import __CHIP__

# Sleep / Power Management HAL
#
# ATmega328P sleep modes (approximate current at 3V, 25C, 8 MHz):
#   sleep_idle()           ~6 mA  -- CPU halted, all peripherals running
#   sleep_adc_noise()      ~1 mA  -- best ADC accuracy; ADC still runs
#   sleep_power_down()   ~0.1 uA  -- deepest sleep; wake on ext-int/WDT/TWI
#   sleep_power_save()   ~0.1 uA  -- power-down + Timer2 async still runs (RTC)
#   sleep_standby()      ~0.1 uA  -- power-down + fast 6-cycle oscillator wake
#
# Each function sets the sleep mode, executes SLEEP, then clears SE on wake.
# Global interrupts (SEI) must be enabled before calling sleep functions, or
# the MCU will sleep forever with no way to wake.
#
# Example (interrupt-driven blink):
#   from pymcu.hal.power import sleep_power_down
#   asm("sei")
#   while True:
#       sleep_power_down()    # wakes on INT0
#       led.toggle()

@inline
def sleep_idle():
    match __CHIP__.name:
        case "atmega328p":
            from pymcu.hal._power.atmega328p import sleep_idle as _sleep_idle
            _sleep_idle()

@inline
def sleep_adc_noise():
    match __CHIP__.name:
        case "atmega328p":
            from pymcu.hal._power.atmega328p import sleep_adc_noise as _sleep_adc_noise
            _sleep_adc_noise()

@inline
def sleep_power_down():
    match __CHIP__.name:
        case "atmega328p":
            from pymcu.hal._power.atmega328p import sleep_power_down as _sleep_power_down
            _sleep_power_down()

@inline
def sleep_power_save():
    match __CHIP__.name:
        case "atmega328p":
            from pymcu.hal._power.atmega328p import sleep_power_save as _sleep_power_save
            _sleep_power_save()

@inline
def sleep_standby():
    match __CHIP__.name:
        case "atmega328p":
            from pymcu.hal._power.atmega328p import sleep_standby as _sleep_standby
            _sleep_standby()
