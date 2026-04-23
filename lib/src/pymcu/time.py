# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

# Software delay functions — no hardware timers, no conflicts.
# Uses @inline + match __CHIP__.arch for dead-code-eliminated,
# architecture-specific tight loops via asm().
#
# Accuracy: ~10-20% at ms level (acceptable for most MCU use cases).
# For precise timing, use hardware timers directly.

from pymcu.types import uint8, uint16, uint32, inline, asm
from pymcu.chips import __CHIP__

@inline
def delay_ms(ms: uint16):
    """Delay for approximately the given number of milliseconds."""
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
    """Software millisecond delay loop for PIC14 architecture."""
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
        i += 1

@inline
def _delay_ms_pic14e(ms: uint8):
    """Software millisecond delay loop for PIC14E architecture."""
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
        i += 1

@inline
def _delay_ms_pic18(ms: uint8):
    """Software millisecond delay loop for PIC18 architecture."""
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
        i += 1

def _delay_1ms_avr():
    """AVR 1ms delay subroutine (non-inline; called once per ms by _delay_ms_avr)."""
    # Non-inline: labels appear exactly once in the assembled output.
    # Nested loop: 21 outer * 255 inner * 3 cycles = 16065 cycles ≈ 1ms at 16MHz.
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

@inline
def _delay_ms_avr(ms: uint16):
    """Software millisecond delay loop for AVR architecture."""
    # Calls the non-inline 1ms helper once per ms so labels are not duplicated
    # across multiple delay_ms() call sites.
    # uint16 counter supports up to 65535ms (~65 seconds).
    i: uint16 = 0
    while i < ms:
        _delay_1ms_avr()
        i += 1

@inline
def _delay_ms_riscv(ms: uint8):
    """Software millisecond delay loop for RISC-V architecture."""
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
        i += 1

@inline
def _delay_ms_pic12(ms: uint8):
    """Software millisecond delay loop for PIC12 architecture."""
    # PIC12 baseline: Same Tcy as PIC14, very limited RAM.
    # DECFSZ+GOTO = 3 Tcy/iter. At 4MHz: 1ms = 1000 Tcy.
    i: uint8 = 0
    while i < ms:
        asm("    MOVLW 0xFF")
        asm("    MOVWF __dly_c1")
        asm("_dly_inner_12:")
        asm("    DECFSZ __dly_c1, F")
        asm("    GOTO _dly_inner_12")
        i += 1

@inline
def delay_us(us: uint8):
    """Delay for approximately the given number of microseconds."""
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
    """Software microsecond delay loop for PIC14 architecture."""
    # PIC14 at 4MHz: Tcy=1us. 1us ~= 1 NOP.
    # Loop overhead is ~7 Tcy so each iteration ≈ 8us at 4MHz.
    # For approximate us-level delays.
    i: uint8 = 0
    while i < us:
        asm("    NOP")
        i += 1

@inline
def _delay_us_pic14e(us: uint8):
    """Software microsecond delay loop for PIC14E architecture."""
    i: uint8 = 0
    while i < us:
        asm("    NOP")
        asm("    NOP")
        i += 1

@inline
def _delay_us_pic18(us: uint8):
    """Software microsecond delay loop for PIC18 architecture."""
    i: uint8 = 0
    while i < us:
        asm("    NOP")
        asm("    NOP")
        i += 1

@inline
def _delay_us_avr(us: uint8):
    """Software microsecond delay loop for AVR architecture."""
    # AVR at 16MHz: 1us = 16 cycles. Loop overhead ~4, so 12 NOPs needed.
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
        i += 1

@inline
def _delay_us_riscv(us: uint8):
    """Software microsecond delay loop for RISC-V architecture."""
    i: uint8 = 0
    while i < us:
        asm("    NOP")
        asm("    NOP")
        asm("    NOP")
        asm("    NOP")
        i += 1

@inline
def _delay_us_pic12(us: uint8):
    """Software microsecond delay loop for PIC12 architecture."""
    i: uint8 = 0
    while i < us:
        asm("    NOP")
        i += 1


@inline
def sleep(seconds: float):
    """Delay for approximately the given number of seconds.

    Accepts compile-time float literals: sleep(0.5) compiles to
    delay_ms(500) with zero runtime float overhead.
    Only constant float values at the call site are supported.
    """
    delay_ms(int(seconds * 1000))


@inline
def millis_init():
    """Initialize the hardware millisecond counter.

    Configures Timer0 (prescaler 64) and registers an overflow ISR that
    increments a uint32 counter once per ~1 ms.  Must be called once before
    using millis() or micros().  AVR (ATmega328P) only.
    """
    match __CHIP__.arch:
        case "avr":
            from pymcu.hal._timer.atmega328p import millis_init as _millis_init_avr
            _millis_init_avr()


@inline
def millis() -> uint32:
    """Return elapsed milliseconds since millis_init() was called.

    Reads a Timer0-overflow counter atomically under CLI/SEI.
    AVR (ATmega328P) only; returns 0 on unsupported architectures.
    """
    match __CHIP__.arch:
        case "avr":
            from pymcu.hal._timer.atmega328p import millis as _millis_avr
            return _millis_avr()
        case _:
            return 0


@inline
def micros() -> uint32:
    """Return elapsed microseconds since millis_init() was called.

    Combines the overflow counter with the current TCNT0 value for
    4 us resolution at 16 MHz / prescaler 64.
    AVR (ATmega328P) only; returns 0 on unsupported architectures.
    """
    match __CHIP__.arch:
        case "avr":
            from pymcu.hal._timer.atmega328p import micros as _micros_avr
            return _micros_avr()
        case _:
            return 0

