# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# RP2040 PWM HAL -- pymcu.hal.rp2040.pwm
#
# Each GPIO maps to a PWM slice (slice = pin >> 1) and a channel (A = even pin,
# B = odd pin). `pin` is a compile-time constant, so all slice-register addresses
# fold to constants when used directly in __init__.

from pymcu.chips.rp2040 import (
    PWM_BASE, PWM_CH_STRIDE, PWM_CH_CSR, PWM_CH_DIV, PWM_CH_CC, PWM_CH_TOP,
    RESETS_RESET_CLR, RESETS_RESET_DONE, RESET_PWM,
    RESET_IO_BANK0, RESET_PADS_BANK0,
    IO_BANK0_BASE, GPIO_FUNC_PWM,
)
from pymcu.types import ptr, uint16, uint32, const, inline

_CLK_SYS = 125000000


class PWM:
    """Hardware PWM on one GPIO, zero-cost abstraction."""

    def __init__(self, pin: const, freq: const = 1000, duty: const = 0):
        self._base = PWM_BASE + ((pin >> 1) & 7) * PWM_CH_STRIDE
        self._chan = pin & 1                       # 0 = A, 1 = B

        reset_mask: uint32 = (1 << RESET_PWM) | (1 << RESET_IO_BANK0) | (1 << RESET_PADS_BANK0)
        RESETS_RESET_CLR.value = reset_mask
        while (RESETS_RESET_DONE.value & reset_mask) != reset_mask:
            pass

        # Inline the const `pin` into every address (so they fold to constants) and
        # the const values directly (a `const` local with no width defaults to uint8
        # and would truncate a 16-bit TOP/compare).
        div: ptr[uint32] = ptr(PWM_BASE + ((pin >> 1) & 7) * PWM_CH_STRIDE + PWM_CH_DIV)
        div.value = 1 << 4                          # DIV = 1.0
        top: ptr[uint32] = ptr(PWM_BASE + ((pin >> 1) & 7) * PWM_CH_STRIDE + PWM_CH_TOP)
        top.value = ((_CLK_SYS // freq) - 1) & 0xFFFF
        cc: ptr[uint32] = ptr(PWM_BASE + ((pin >> 1) & 7) * PWM_CH_STRIDE + PWM_CH_CC)
        if (pin & 1) == 0:
            cc.value = (((_CLK_SYS // freq) * duty) >> 16) & 0xFFFF
        else:
            cc.value = ((((_CLK_SYS // freq) * duty) >> 16) & 0xFFFF) << 16

        # Route the pin to the PWM function.
        ctrl: ptr[uint32] = ptr(IO_BANK0_BASE + 8 * pin + 4)
        ctrl.value = GPIO_FUNC_PWM

        # Enable the slice.
        csr: ptr[uint32] = ptr(PWM_BASE + ((pin >> 1) & 7) * PWM_CH_STRIDE + PWM_CH_CSR)
        csr.value = 1

    @inline
    def set_duty(self, duty: uint16):
        # Scale 0..65535 to the slice TOP and write the channel compare.
        top: ptr[uint32] = ptr(self._base + PWM_CH_TOP)
        compare: uint32 = ((top.value + 1) * duty) >> 16
        cc: ptr[uint32] = ptr(self._base + PWM_CH_CC)
        if self._chan == 0:
            cc.value = (cc.value & 0xFFFF0000) | (compare & 0xFFFF)
        else:
            cc.value = (cc.value & 0x0000FFFF) | ((compare & 0xFFFF) << 16)

    @inline
    def stop(self):
        csr: ptr[uint32] = ptr(self._base + PWM_CH_CSR)
        csr.value = 0
