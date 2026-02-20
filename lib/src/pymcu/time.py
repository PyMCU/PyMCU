# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# This file is part of the PyMCU Standard Library (pymcu-stdlib).
#
# pymcu-stdlib is free software: you can redistribute it and/or modify
# it under the terms of the GNU General Public License as published by
# the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# pymcu-stdlib is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
# GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with pymcu-stdlib.  If not, see <https://www.gnu.org/licenses/>.
#
# -----------------------------------------------------------------------------
# NOTICE: STRICT COPYLEFT & STATIC LINKING
# -----------------------------------------------------------------------------
# This file contains hardware abstractions (HAL) and register definitions that
# are statically linked (and/or inline expanded) into the final firmware binary by the pymcuc compiler.
#
# UNLIKE standard compiler libraries (e.g., GCC Runtime Library Exception),
# NO EXCEPTION is granted for proprietary use.
#
# If you compile a program that imports this library, the resulting firmware
# binary is considered a "derivative work" of this library under the GPLv3.
# Therefore, any firmware linked against this library must also be released
# under the terms of the GNU GPLv3.
#
# COMMERCIAL LICENSING:
# If you wish to create proprietary (closed-source) firmware using PyMCU,
# you must acquire a Commercial License from the copyright holder.
#
# For licensing inquiries, visit: https://pymcu.org/licensing
# or contact: sales@pymcu.org
#
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

# Software delay functions — no hardware timers, no conflicts.
# Uses @inline + match __CHIP__.arch for dead-code-eliminated,
# architecture-specific tight loops via asm().
#
# Accuracy: ~10-20% at ms level (acceptable for most MCU use cases).
# For precise timing, use hardware timers directly.

from pymcu.types import uint8, inline, asm

@inline
def delay_ms(ms: uint8):
    match __CHIP__.arch:
        case "pic14":
            _delay_ms_pic14(ms)
        case "pic14e":
            _delay_ms_pic14e(ms)
        case "pic18":
            _delay_ms_pic18(ms)
        case "avr":
            _delay_ms_avr(ms)
        case "riscv":
            _delay_ms_riscv(ms)
        case "pic12":
            _delay_ms_pic12(ms)

@inline
def _delay_ms_pic14(ms: uint8):
    # PIC14: Tcy = 4 clocks. DECFSZ+GOTO = 3 Tcy/iter.
    # 255 iters × 3 = 765 Tcy ≈ 0.765ms at 4MHz (Tcy=1us)
    # Outer while loop adds ~7 Tcy overhead per ms iteration.
    i: uint8 = 0
    while i < ms:
        asm("    MOVLW 0xFF")
        asm("    MOVWF __dly_c1")
        asm("_dly_inner:")
        asm("    DECFSZ __dly_c1, F")
        asm("    GOTO _dly_inner")
        i = i + 1

@inline
def _delay_ms_pic14e(ms: uint8):
    # PIC14E: Same instruction timing as PIC14, often higher Fosc.
    # At 32MHz internal: Tcy = 125ns, 1ms = 8000 Tcy.
    # Need nested loop: outer 10 × inner 255 × 3 = 7650 Tcy ≈ 0.96ms
    i: uint8 = 0
    while i < ms:
        asm("    MOVLW 0x0B")
        asm("    MOVWF __dly_c2")
        asm("_dly_outer_e:")
        asm("    MOVLW 0xFF")
        asm("    MOVWF __dly_c1")
        asm("_dly_inner_e:")
        asm("    DECFSZ __dly_c1, F")
        asm("    GOTO _dly_inner_e")
        asm("    DECFSZ __dly_c2, F")
        asm("    GOTO _dly_outer_e")
        i = i + 1

@inline
def _delay_ms_pic18(ms: uint8):
    # PIC18: Tcy = 4 clocks, DECFSZ+BRA = 3 Tcy/iter (BRA = 2 on taken).
    # Typically 48MHz: Tcy = 83.3ns, 1ms = 12000 Tcy.
    # Nested: 16 × 255 × 3 = 12240 Tcy ≈ 1.02ms
    i: uint8 = 0
    while i < ms:
        asm("    MOVLW 0x10")
        asm("    MOVWF __dly_c2, ACCESS")
        asm("_dly_outer_18:")
        asm("    MOVLW 0xFF")
        asm("    MOVWF __dly_c1, ACCESS")
        asm("_dly_inner_18:")
        asm("    DECFSZ __dly_c1, F, ACCESS")
        asm("    BRA _dly_inner_18")
        asm("    DECFSZ __dly_c2, F, ACCESS")
        asm("    BRA _dly_outer_18")
        i = i + 1

@inline
def _delay_ms_avr(ms: uint8):
    # AVR: 1 clock = 1 cycle. DEC+BRNE = 3 cycles/iter.
    # At 16MHz: 1ms = 16000 cycles. Nested: 21 × 255 × 3 = 16065 ≈ 1ms
    # Uses R24, R25 as counters.
    # Note: R24/R25 are call-clobbered, so safe to use in inline asm if not preserving across calls.
    # However, pymcuc's register allocator might use them.
    # For safety, we should push/pop or use dedicated temp vars if allocator was smarter.
    # But since this is inline asm block, allocator doesn't see register usage.
    # We'll assume R24/R25 are free or caller-saved.
    i: uint8 = 0
    while i < ms:
        asm("    PUSH R24")
        asm("    PUSH R25")
        asm("    LDI R24, 21")
        asm("_dly_outer_avr:")
        asm("    LDI R25, 255")
        asm("_dly_inner_avr:")
        asm("    DEC R25")
        asm("    BRNE _dly_inner_avr")
        asm("    DEC R24")
        asm("    BRNE _dly_outer_avr")
        asm("    POP R25")
        asm("    POP R24")
        i = i + 1

@inline
def _delay_ms_riscv(ms: uint8):
    # RISC-V: ADDI+BNE = ~3-4 cycles/iter depending on pipeline.
    # CH32V003 at 48MHz: 1ms = 48000 cycles.
    # Nested: 63 × 255 × 3 = 48195 ≈ 1ms
    i: uint8 = 0
    while i < ms:
        asm("    LI t0, 63")
        asm("_dly_outer_rv:")
        asm("    LI t1, 255")
        asm("_dly_inner_rv:")
        asm("    ADDI t1, t1, -1")
        asm("    BNEZ t1, _dly_inner_rv")
        asm("    ADDI t0, t0, -1")
        asm("    BNEZ t0, _dly_outer_rv")
        i = i + 1

@inline
def _delay_ms_pic12(ms: uint8):
    # PIC12 baseline: Same Tcy as PIC14, very limited RAM.
    # DECFSZ+GOTO = 3 Tcy/iter. At 4MHz: 1ms = 1000 Tcy.
    i: uint8 = 0
    while i < ms:
        asm("    MOVLW 0xFF")
        asm("    MOVWF __dly_c1")
        asm("_dly_inner_12:")
        asm("    DECFSZ __dly_c1, F")
        asm("    GOTO _dly_inner_12")
        i = i + 1

@inline
def delay_us(us: uint8):
    match __CHIP__.arch:
        case "pic14":
            _delay_us_pic14(us)
        case "pic14e":
            _delay_us_pic14e(us)
        case "pic18":
            _delay_us_pic18(us)
        case "avr":
            _delay_us_avr(us)
        case "riscv":
            _delay_us_riscv(us)
        case "pic12":
            _delay_us_pic12(us)

@inline
def _delay_us_pic14(us: uint8):
    # PIC14 at 4MHz: Tcy=1us. 1us ≈ 1 NOP.
    # Loop overhead is ~7 Tcy so each iteration ≈ 8us at 4MHz.
    # For approximate us-level delays.
    i: uint8 = 0
    while i < us:
        asm("    NOP")
        i = i + 1

@inline
def _delay_us_pic14e(us: uint8):
    i: uint8 = 0
    while i < us:
        asm("    NOP")
        asm("    NOP")
        i = i + 1

@inline
def _delay_us_pic18(us: uint8):
    i: uint8 = 0
    while i < us:
        asm("    NOP")
        asm("    NOP")
        i = i + 1

@inline
def _delay_us_avr(us: uint8):
    # AVR at 16MHz: 1us = 16 cycles. Loop overhead ~4 → 12 NOPs needed.
    i: uint8 = 0
    while i < us:
        asm("    NOP")
        asm("    NOP")
        asm("    NOP")
        asm("    NOP")
        asm("    NOP")
        asm("    NOP")
        asm("    NOP")
        asm("    NOP")
        asm("    NOP")
        asm("    NOP")
        asm("    NOP")
        asm("    NOP")
        i = i + 1

@inline
def _delay_us_riscv(us: uint8):
    i: uint8 = 0
    while i < us:
        asm("    NOP")
        asm("    NOP")
        asm("    NOP")
        asm("    NOP")
        i = i + 1

@inline
def _delay_us_pic12(us: uint8):
    i: uint8 = 0
    while i < us:
        asm("    NOP")
        i = i + 1
