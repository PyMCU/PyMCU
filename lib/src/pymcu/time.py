# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

# Software delay functions -- no hardware timers, no conflicts.
# Uses @inline + match __CHIP__.arch for dead-code-eliminated,
# architecture-specific tight loops via asm().
#
# Accuracy: <0.1% at 1, 8, 12, 16, 20 MHz; defaults to 16 MHz for other
# AVR frequencies. For precise timing, use hardware timers directly.

from pymcu.types import uint8, uint16, uint32, inline, asm
from pymcu.chips import __CHIP__, __FREQ__

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
    # 255 iters x 3 = 765 Tcy ~ 0.765ms at 4MHz (Tcy=1us)
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
    """Software millisecond delay loop for PIC14E architecture."""
    # PIC14E: Same instruction timing as PIC14, often higher Fosc.
    # At 32MHz internal: Tcy = 125ns, 1ms = 8000 Tcy.
    # Need nested loop: outer 10 x inner 255 x 3 = 7650 Tcy ~ 0.96ms
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
    """Software millisecond delay loop for PIC18 architecture."""
    # PIC18: Tcy = 4 clocks, DECFSZ+BRA = 3 Tcy/iter (BRA = 2 on taken).
    # Typically 48MHz: Tcy = 83.3ns, 1ms = 12000 Tcy.
    # Nested: 16 x 255 x 3 = 12240 Tcy ~ 1.02ms
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

def _delay_1ms_avr_1mhz():
    # 1 MHz: outer=2, inner=163 -> 1000 cycles = 1.000 ms (+0.000%)
    asm("    PUSH R24")
    asm("    PUSH R25")
    asm("    LDI R24, 2")
    asm("_dly_o1mhz:")
    asm("    LDI R25, 163")
    asm("_dly_i1mhz:")
    asm("    DEC R25")
    asm("    BRNE _dly_i1mhz")
    asm("    DEC R24")
    asm("    BRNE _dly_o1mhz")
    asm("    POP R25")
    asm("    POP R24")

def _delay_1ms_avr_8mhz():
    # 8 MHz: outer=11, inner=241 -> 8002 cycles ~ 1ms (+0.025%)
    asm("    PUSH R24")
    asm("    PUSH R25")
    asm("    LDI R24, 11")
    asm("_dly_o8mhz:")
    asm("    LDI R25, 241")
    asm("_dly_i8mhz:")
    asm("    DEC R25")
    asm("    BRNE _dly_i8mhz")
    asm("    DEC R24")
    asm("    BRNE _dly_o8mhz")
    asm("    POP R25")
    asm("    POP R24")

def _delay_1ms_avr_12mhz():
    # 12 MHz: outer=17, inner=234 -> 12001 cycles ~ 1ms (+0.008%)
    asm("    PUSH R24")
    asm("    PUSH R25")
    asm("    LDI R24, 17")
    asm("_dly_o12mhz:")
    asm("    LDI R25, 234")
    asm("_dly_i12mhz:")
    asm("    DEC R25")
    asm("    BRNE _dly_i12mhz")
    asm("    DEC R24")
    asm("    BRNE _dly_o12mhz")
    asm("    POP R25")
    asm("    POP R24")

def _delay_1ms_avr_16mhz():
    # 16 MHz: outer=24, inner=221 -> 16000 cycles = 1.000 ms (+0.000%)
    asm("    PUSH R24")
    asm("    PUSH R25")
    asm("    LDI R24, 24")
    asm("_dly_o16mhz:")
    asm("    LDI R25, 221")
    asm("_dly_i16mhz:")
    asm("    DEC R25")
    asm("    BRNE _dly_i16mhz")
    asm("    DEC R24")
    asm("    BRNE _dly_o16mhz")
    asm("    POP R25")
    asm("    POP R24")

def _delay_1ms_avr_20mhz():
    # 20 MHz: outer=30, inner=221 -> 19996 cycles ~ 1ms (-0.020%)
    asm("    PUSH R24")
    asm("    PUSH R25")
    asm("    LDI R24, 30")
    asm("_dly_o20mhz:")
    asm("    LDI R25, 221")
    asm("_dly_i20mhz:")
    asm("    DEC R25")
    asm("    BRNE _dly_i20mhz")
    asm("    DEC R24")
    asm("    BRNE _dly_o20mhz")
    asm("    POP R25")
    asm("    POP R24")

def _delay_ms_avr(ms: uint16):
    # Dispatch to the frequency-specific non-inline 1ms helper.
    # match __FREQ__ is dead-code-eliminated at compile time -- only the
    # matching branch survives in the assembled output.
    # uint16 counter supports up to 65535ms (~65 seconds).
    i: uint16 = 0
    while i < ms:
        match __FREQ__:
            case 1_000_000:
                _delay_1ms_avr_1mhz()
            case 8_000_000:
                _delay_1ms_avr_8mhz()
            case 12_000_000:
                _delay_1ms_avr_12mhz()
            case 20_000_000:
                _delay_1ms_avr_20mhz()
            case _:
                _delay_1ms_avr_16mhz()
        i = i + 1

@inline
def _delay_ms_riscv(ms: uint8):
    """Software millisecond delay loop for RISC-V architecture."""
    # RISC-V: ADDI+BNE = ~3-4 cycles/iter depending on pipeline.
    # CH32V003 at 48MHz: 1ms = 48000 cycles.
    # Nested: 63 x 255 x 3 = 48195 ~ 1ms
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
        i = i + 1

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
    # Loop overhead is ~7 Tcy so each iteration ~ 8us at 4MHz.
    # For approximate us-level delays.
    i: uint8 = 0
    while i < us:
        asm("    NOP")
        i = i + 1

@inline
def _delay_us_pic14e(us: uint8):
    """Software microsecond delay loop for PIC14E architecture."""
    i: uint8 = 0
    while i < us:
        asm("    NOP")
        asm("    NOP")
        i = i + 1

@inline
def _delay_us_pic18(us: uint8):
    """Software microsecond delay loop for PIC18 architecture."""
    i: uint8 = 0
    while i < us:
        asm("    NOP")
        asm("    NOP")
        i = i + 1

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
        i = i + 1

@inline
def _delay_us_riscv(us: uint8):
    """Software microsecond delay loop for RISC-V architecture."""
    i: uint8 = 0
    while i < us:
        asm("    NOP")
        asm("    NOP")
        asm("    NOP")
        asm("    NOP")
        i = i + 1

@inline
def _delay_us_pic12(us: uint8):
    """Software microsecond delay loop for PIC12 architecture."""
    i: uint8 = 0
    while i < us:
        asm("    NOP")
        i = i + 1


@inline
def millis_init():
    """Initialize the hardware millisecond counter.

    Configures Timer0 (prescaler 64) and registers an overflow ISR that
    increments a uint32 counter once per ~1 ms.  Must be called once before
    using millis() or micros().  AVR (ATmega328P) only.
    """
    match __CHIP__.arch:
        case "avr":
            from pymcu.hal.avr.atmega328p_timer import millis_init as _millis_init_avr
            _millis_init_avr()


@inline
def millis() -> uint32:
    """Return elapsed milliseconds since millis_init() was called.

    Reads a Timer0-overflow counter atomically under CLI/SEI.
    AVR (ATmega328P) only; returns 0 on unsupported architectures.
    """
    match __CHIP__.arch:
        case "avr":
            from pymcu.hal.avr.atmega328p_timer import millis as _millis_avr
            return _millis_avr()
        case _:
            return 0
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
            from pymcu.hal.avr.atmega328p_timer import micros as _micros_avr
            return _micros_avr()
        case _:
            return 0
    return 0

