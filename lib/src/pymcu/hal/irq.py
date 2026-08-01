# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# hal/irq.py -- Global interrupt enable / disable, zero-cost abstraction
#
# Provides architecture-neutral functions to control the global interrupt flag.
# Each function is @inline and folds away to a single instruction at compile time.
#
# Architecture mapping:
#   avr      -- SEI / CLI  (I-flag in SREG bit 7)
#   arm      -- CPSIE I / CPSID I  (PRIMASK on Cortex-M0+/M33)
#   pic14    -- BSF/BCF INTCON, GIE  (INTCON = 0x0B, bit 7)
#   pic14e   -- same as pic14
#   pic18    -- BSF/BCF INTCON, GIE  (INTCON = 0xFF2, bit 7)
#   riscv    -- csrsi/csrci mstatus, 8  (MIE = bit 3 of mstatus)
#   pic12    -- no interrupt controller; functions are no-ops
#
# Usage:
#   from pymcu.hal.irq import enable_interrupts, disable_interrupts
#
#   enable_interrupts()
#   while True:
#       do_work()
#
#   # Critical section:
#   disable_interrupts()
#   shared_state += 1
#   enable_interrupts()

from pymcu.types import uint8, ptr, inline, asm
from pymcu.chips import __CHIP__


@inline
def enable_interrupts():
    """Enable the global interrupt flag for the current architecture.

    avr:    SEI  -- sets I-flag in SREG
    arm:    CPSIE I -- clears PRIMASK on Cortex-M
    pic14/e: BSF INTCON, GIE  (INTCON bit 7)
    pic18:  BSF INTCON, GIE   (INTCON bit 7)
    riscv:  csrsi mstatus, 8  (sets MIE)
    pic12:  no-op             (no interrupt controller)
    """
    match __CHIP__.arch:
        case "avr":
            asm("SEI")
        case "arm":
            # Cortex-M: CPSIE I clears PRIMASK, unmasking every configurable
            # exception. Paired with the CPSID I in disable_interrupts().
            asm("cpsie i")
        case "pic14" | "pic14e":
            intcon: ptr[uint8] = ptr(0x0B)
            intcon[7] = 1
        case "pic18":
            intcon: ptr[uint8] = ptr(0xFF2)
            intcon[7] = 1
        case "riscv":
            asm("csrsi mstatus, 8")
        case _:
            pass  # pic12 and others: no interrupt controller


@inline
def disable_interrupts():
    """Disable the global interrupt flag for the current architecture.

    avr:    CLI  -- clears I-flag in SREG
    arm:    CPSID I -- sets PRIMASK on Cortex-M
    pic14/e: BCF INTCON, GIE  (INTCON bit 7)
    pic18:  BCF INTCON, GIE   (INTCON bit 7)
    riscv:  csrci mstatus, 8  (clears MIE)
    pic12:  no-op             (no interrupt controller)
    """
    match __CHIP__.arch:
        case "avr":
            asm("CLI")
        case "arm":
            # Cortex-M: CPSID I sets PRIMASK, masking every configurable
            # exception (NMI and HardFault stay enabled by design).
            asm("cpsid i")
        case "pic14" | "pic14e":
            intcon: ptr[uint8] = ptr(0x0B)
            intcon[7] = 0
        case "pic18":
            intcon: ptr[uint8] = ptr(0xFF2)
            intcon[7] = 0
        case "riscv":
            asm("csrci mstatus, 8")
        case _:
            pass  # pic12 and others: no interrupt controller
