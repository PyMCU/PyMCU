# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# RP2040 GPIO HAL -- pymcu.hal.rp2040.gpio
#
# All 30 GPIOs share one set of single-cycle-IO (SIO) registers; the pin number
# IS the bit index, so there is no per-pin register selection (unlike AVR). The
# pin number is a compile-time constant, so `1 << pin` and the per-pin IO_BANK0 /
# PADS_BANK0 register addresses fold to constants -- the whole Pin abstraction is
# zero-cost (every method is @inline and lowers to a single volatile MMIO store).

from pymcu.chips.rp2040 import (
    IO_BANK0_BASE, PADS_BANK0_BASE,
    RESETS_RESET_CLR, RESETS_RESET_DONE,
    SIO_GPIO_OE_SET, SIO_GPIO_OE_CLR,
    SIO_GPIO_OUT_SET, SIO_GPIO_OUT_CLR, SIO_GPIO_OUT_XOR, SIO_GPIO_IN,
    GPIO_FUNC_SIO, RESET_IO_BANK0, RESET_PADS_BANK0,
)
from pymcu.types import ptr, uint32, uint8, const, inline
from pymcu.exceptions import CompileError


class Pin:
    OUT        = 0
    IN         = 1
    OPEN_DRAIN = 2   # not natively supported; accepted for API compat (no-op in mode())

    PULL_UP   = 1
    PULL_DOWN = 2

    def __init__(self, pin: uint8, mode: const[uint8] = 0,
                 pull: const = -1, value: const = -1):
        # `pin` may be a runtime value (Pin(n) where n is a variable). A constant pin
        # still const-folds the address math below (verified: Pin(25).toggle() lowers
        # to a single `store i32 33554432` -- zero-cost), so only a genuinely runtime
        # pin pays for the 4*pin / 8*pin / 1<<pin instructions.
        self._pin = pin

        # Bring IO_BANK0 and PADS_BANK0 out of reset and wait until ready.
        RESETS_RESET_CLR.value = (1 << RESET_IO_BANK0) | (1 << RESET_PADS_BANK0)
        while (RESETS_RESET_DONE.value & ((1 << RESET_IO_BANK0) | (1 << RESET_PADS_BANK0))) != ((1 << RESET_IO_BANK0) | (1 << RESET_PADS_BANK0)):
            pass

        # Pad: input-enable on, output-disable off (bit6 = IE).
        pad: ptr[uint32] = ptr(PADS_BANK0_BASE + 4 + 4 * pin)
        pad.value = 1 << 6

        # Route the pin to SIO (GPIOn_CTRL FUNCSEL = 5).
        ctrl: ptr[uint32] = ptr(IO_BANK0_BASE + 8 * pin + 4)
        ctrl.value = GPIO_FUNC_SIO

        # Direction.
        if mode == 0:
            SIO_GPIO_OE_SET.value = 1 << pin
        else:
            SIO_GPIO_OE_CLR.value = 1 << pin

        if value != -1:
            if value == 0:
                SIO_GPIO_OUT_CLR.value = 1 << pin
            else:
                SIO_GPIO_OUT_SET.value = 1 << pin

    @inline
    def high(self):
        SIO_GPIO_OUT_SET.value = 1 << self._pin

    @inline
    def low(self):
        SIO_GPIO_OUT_CLR.value = 1 << self._pin

    @inline
    def on(self):
        self.high()

    @inline
    def off(self):
        self.low()

    @inline
    def toggle(self):
        SIO_GPIO_OUT_XOR.value = 1 << self._pin

    @inline
    def mode(self, m: const[uint8]):
        # Reconfigure direction without re-running pad/mux init.
        if m == 0:  # OUT
            SIO_GPIO_OE_SET.value = 1 << self._pin
        else:       # IN (OPEN_DRAIN falls here: leaves FUNCSEL intact, floats output)
            SIO_GPIO_OE_CLR.value = 1 << self._pin

    @inline
    def pull(self, p: const[uint8]):
        # Rewrite pad IE + pull bits only; preserves drive strength.
        # PADS layout: bit6=IE, bit3=PUE, bit2=PDE.
        pad: ptr[uint32] = ptr(PADS_BANK0_BASE + 4 + 4 * self._pin)
        if p == 1:       # PULL_UP
            pad.value = 0x48
        elif p == 2:     # PULL_DOWN
            pad.value = 0x44
        else:            # no pull
            pad.value = 0x40

    @inline
    def value(self, x: const = -1) -> uint32:
        if x == -1:
            return (SIO_GPIO_IN.value >> self._pin) & 1
        elif x == 0:
            SIO_GPIO_OUT_CLR.value = 1 << self._pin
        else:
            SIO_GPIO_OUT_SET.value = 1 << self._pin

    @inline
    def pulse_in(self, state: uint8, timeout_us: uint16 = 1000) -> uint16:
        # Wait for the pin to reach `state`, then measure how long it holds it, in
        # microseconds (the bit-bang primitive behind machine.time_pulse_us, used by
        # the DHT driver). Returns 0 on timeout. Rides the chip-wide 1 MHz timer
        # (TIMERAWL @ TIMER_BASE + 0x28), so the same flavor code runs on RP2040 too.
        mask: uint32 = 1 << self._pin
        timer: ptr[uint32] = ptr(0x40054028)
        want: uint32 = 0
        if state != 0:
            want = mask
        # 1. wait for the level to appear
        t0: uint32 = timer.value
        while (SIO_GPIO_IN.value & mask) != want:
            if (timer.value - t0) > timeout_us:
                return 0
        # 2. measure how long it stays
        start: uint32 = timer.value
        while (SIO_GPIO_IN.value & mask) == want:
            if (timer.value - start) > timeout_us:
                return 0
        return timer.value - start
