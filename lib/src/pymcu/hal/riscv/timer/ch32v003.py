# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# CH32V003 time base on SysTick -- millis() and micros().
#
# SysTick (CH32V003 Reference Manual, "System timer (SysTick)": CTLR 0xE000F000, SR +0x04,
# CNT +0x08, CMP +0x10) counts HCLK cycles up from 0. With STRE set it restarts from 0 each time
# CNT reaches CMP and raises CNTIF, so CMP = HCLK / 1000 makes it overflow exactly once per
# millisecond with no drift: the interrupt (PFIC vector 12) only counts whole milliseconds, and
# micros() adds the fraction from CNT, divided by the cycles per microsecond.
#
# That divisor is a compile-time constant, so HCLK has to be a whole number of MHz (every clock
# this part runs at: 8, 24 and 48 MHz); any other frequency is refused rather than ticking
# at a slightly wrong rate.
# -----------------------------------------------------------------------------
from pymcu.chips import __FREQ__
from pymcu.chips.ch32v003 import SYSTICK_CTLR, SYSTICK_SR, SYSTICK_CNT, SYSTICK_CMP
from pymcu.exceptions import CompileError
from pymcu.types import uint8, uint32, ptr, inline, compile_isr

_PFIC_IENR1: ptr[uint32] = ptr(0xE000E100)    # IRQ 0-31 enable-set; SysTick is IRQ 12
_SYSTICK_IRQ = 12

_millis_count: uint32 = 0


def _systick_isr():
    # Non-inline: compiled once and placed at the SysTick vector by millis_init().
    global _millis_count
    SYSTICK_SR.value = 0                       # CNTIF is cleared by writing 0
    _millis_count = _millis_count + 1


@inline
def millis_init():
    """Start the time base: SysTick overflowing once per millisecond, interrupt enabled."""
    if __FREQ__ % 1000000 != 0:
        raise CompileError(
            "the CH32V003 time base counts whole microseconds of HCLK, so the clock has to be "
            "a whole number of MHz.")
    SYSTICK_CTLR.value = 0
    SYSTICK_CMP.value = __FREQ__ // 1000
    SYSTICK_SR.value = 0
    SYSTICK_CTLR.value = 0x2F                  # INIT | STRE | STCLK=HCLK | STIE | STE
    _PFIC_IENR1.value = 1 << _SYSTICK_IRQ
    compile_isr(_systick_isr, 12)              # mstatus.MIE is already set when main starts


def _micros_now() -> uint32:
    # Shared by every micros() call. The count and the counter are read as a pair that a
    # millisecond boundary did not fall inside: if the compare flag moved or the count changed
    # between the reads, read again. A flag that is set but whose interrupt has not run yet
    # means one more whole millisecond than the count says.
    while True:
        ms: uint32 = _millis_count
        f1: uint32 = SYSTICK_SR.value & 1
        c: uint32 = SYSTICK_CNT.value
        f2: uint32 = SYSTICK_SR.value & 1
        if ms == _millis_count and f1 == f2:
            return (ms + f1) * 1000 + c // (__FREQ__ // 1000000)


@inline
def micros() -> uint32:
    return _micros_now()


@inline
def millis() -> uint32:
    return _millis_count
