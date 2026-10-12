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
from pymcu.types import ptr, uint8, uint16, uint32, const, inline

_CLK_SYS = 125000000


class PWM:
    """Hardware PWM on one GPIO, zero-cost abstraction."""

    def __init__(self, pin: const, freq: uint32 = 1000, duty: uint16 = 0, duty_u16: uint16 = 0,
                 invert: const[uint8] = 0):
        # `pin` is the only compile-time constant: it selects the slice and the register
        # addresses. The frequency and the duty may be run-time values, so a driver can
        # retune the output (set_freq / set_duty_u16). duty and duty_u16 are both 0..65535 =
        # 0..100 % on this chip; duty_u16 is the name every architecture's HAL takes (the
        # compat layers pass it) and wins when non-zero. invert flips the output level.
        if duty_u16 != 0:
            duty = duty_u16
        self._base = PWM_BASE + ((pin >> 1) & 7) * PWM_CH_STRIDE
        self._chan = pin & 1                       # 0 = A, 1 = B
        self._csr = 1 | (invert << (2 + (pin & 1)))

        reset_mask: uint32 = (1 << RESET_PWM) | (1 << RESET_IO_BANK0) | (1 << RESET_PADS_BANK0)
        RESETS_RESET_CLR.value = reset_mask
        while (RESETS_RESET_DONE.value & reset_mask) != reset_mask:
            pass

        self.set_freq(freq)
        self.set_duty_u16(duty)

        # Route the pin to the PWM function.
        ctrl: ptr[uint32] = ptr(IO_BANK0_BASE + 8 * pin + 4)
        ctrl.value = GPIO_FUNC_PWM

        # Enable the slice.
        csr: ptr[uint32] = ptr(PWM_BASE + ((pin >> 1) & 7) * PWM_CH_STRIDE + PWM_CH_CSR)
        csr.value = self._csr

    @inline
    def set_freq(self, freq: uint32):
        # The counter wraps at TOP + 1 system clocks. A period of up to 65536 clocks fits
        # TOP with the divider at 1.0; a longer one needs the 8.4 fixed-point divider, chosen
        # as the smallest that brings TOP + 1 back under 65536. The slowest output is about
        # 8 Hz (divider 255.9375, TOP 65535); a lower request runs at that.
        if freq == 0:
            freq = 1
        period: uint32 = _CLK_SYS // freq
        if period == 0:
            period = 1
        div16: uint32 = 16
        if period > 65536:
            div16 = (period >> 12) + 1
            if div16 > 4095:
                div16 = 4095
        top: uint32 = ((period * 16) // div16) - 1
        if top > 65535:
            top = 65535
        div: ptr[uint32] = ptr(self._base + PWM_CH_DIV)
        div.value = div16
        topr: ptr[uint32] = ptr(self._base + PWM_CH_TOP)
        topr.value = top

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
    def set_duty_u16(self, duty_u16: uint16):
        # The 16-bit entry is this chip's native one.
        self.set_duty(duty_u16)

    @inline
    def start(self):
        csr: ptr[uint32] = ptr(self._base + PWM_CH_CSR)
        csr.value = self._csr

    @inline
    def deinit(self):
        self.stop()

    @inline
    def stop(self):
        csr: ptr[uint32] = ptr(self._base + PWM_CH_CSR)
        csr.value = 0
