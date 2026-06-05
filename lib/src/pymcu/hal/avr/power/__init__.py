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
    sleep_idle,
    sleep_adc_noise,
    sleep_power_down,
    sleep_power_save,
    sleep_standby,
    sleep_extended_standby,
)
