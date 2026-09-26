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
from pymcu.exceptions import CompileError


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


# A critical section that nests: save the interrupt state, disable, and later put back
# exactly what was saved. enable_interrupts() after a nested section re-enables what an
# outer one still needs off, so `s1 = save(); s2 = save(); restore(s2)` left the I-flag
# set with the outer section still open (PyMCU#353).
#
# The state is opaque to the caller: nonzero means "interrupts were on". Architectures
# whose flag lives in a core register a program cannot read here (PRIMASK, mstatus)
# refuse instead of answering a state they did not read.

@inline
def save_and_disable_interrupts() -> uint8:
    """Disable interrupts and return the state to hand to restore_interrupts().

    avr:     SREG & 0x80 (the I-flag), then CLI
    pic14/e: INTCON & 0x80 (GIE), then BCF INTCON, GIE
    pic18:   INTCON & 0x80 (GIE), then BCF INTCON, GIE
    pic12:   0 (no interrupt controller)
    """
    match __CHIP__.arch:
        case "avr":
            sreg: ptr[uint8] = ptr(0x5F)
            state: uint8 = sreg.value & 0x80
            asm("CLI")
            return state
        case "pic14" | "pic14e":
            intcon: ptr[uint8] = ptr(0x0B)
            state: uint8 = intcon.value & 0x80
            intcon[7] = 0
            return state
        case "pic18":
            intcon: ptr[uint8] = ptr(0xFF2)
            state: uint8 = intcon.value & 0x80
            intcon[7] = 0
            return state
        case "arm" | "riscv":
            raise CompileError(
                "save_and_disable_interrupts: reading the interrupt mask (PRIMASK on "
                "Cortex-M, mstatus.MIE on RISC-V) is not wired up on this architecture, "
                "so a nested critical section cannot be restored. Use "
                "disable_interrupts()/enable_interrupts() around a section that does not nest.")
        case _:
            return 0


@inline
def restore_interrupts(state: uint8):
    """Put back the interrupt state save_and_disable_interrupts() returned."""
    match __CHIP__.arch:
        case "avr":
            if state != 0:
                asm("SEI")
            else:
                asm("CLI")
        case "pic14" | "pic14e":
            intcon: ptr[uint8] = ptr(0x0B)
            if state != 0:
                intcon[7] = 1
            else:
                intcon[7] = 0
        case "pic18":
            intcon: ptr[uint8] = ptr(0xFF2)
            if state != 0:
                intcon[7] = 1
            else:
                intcon[7] = 0
        case "arm" | "riscv":
            raise CompileError(
                "restore_interrupts: reading the interrupt mask is not wired up on this "
                "architecture; see save_and_disable_interrupts.")
        case _:
            pass
