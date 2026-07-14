# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# RP2040 ADC HAL -- pymcu.hal.rp2040.adc
#
# 5-channel 12-bit SAR ADC. Channels 0-3 are GP26-GP29; channel 4 is the
# internal temperature sensor. Fixed-address MMIO, so every access folds to a
# volatile load/store.

from pymcu.chips.rp2040 import (
    ADC_CS, ADC_RESULT,
    RESETS_RESET_CLR, RESETS_RESET_DONE, RESET_ADC,
    ADC_CS_EN, ADC_CS_TS_EN, ADC_CS_START_ONCE, ADC_CS_READY,
)
from pymcu.types import uint8, uint16, const, inline


class AnalogPin:
    """One ADC input channel (0-3 = GP26-GP29, 4 = temperature)."""

    def __init__(self, channel: const[uint8] = 0):
        self._ch = channel

        RESETS_RESET_CLR.value = 1 << RESET_ADC
        while (RESETS_RESET_DONE.value & (1 << RESET_ADC)) == 0:
            pass

        # Power on the ADC (and the temperature sensor for channel 4).
        if channel == 4:
            ADC_CS.value = (1 << ADC_CS_EN) | (1 << ADC_CS_TS_EN)
        else:
            ADC_CS.value = 1 << ADC_CS_EN
        while ((ADC_CS.value >> ADC_CS_READY) & 1) == 0:
            pass

    @inline
    def read(self) -> uint16:
        # Select the channel (AINSEL at CS[14:12]), trigger one conversion, wait.
        ADC_CS.value = (ADC_CS.value & 0xFFFF8FFF) | (self._ch << 12) | (1 << ADC_CS_EN)
        ADC_CS.value = ADC_CS.value | (1 << ADC_CS_START_ONCE)
        while ((ADC_CS.value >> ADC_CS_READY) & 1) == 0:
            pass
        return ADC_RESULT.value & 0xFFF
