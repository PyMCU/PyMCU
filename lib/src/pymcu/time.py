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

from pymcu.types import uint8, uint16, uint32, const, inline, asm, ptr
from pymcu.chips import __CHIP__, __FREQ__
from pymcu.exceptions import CompileError

@inline
def sleep(seconds: const[float]):
    """Python's sleep, in seconds, folded to the millisecond delay.

    The argument has to be known at compile time: there is no float
    arithmetic on the delay path, and the whole point of the fold is that
    sleep(0.5) costs exactly what delay_ms(500) costs.
    """
    delay_ms(uint16(seconds * 1000.0))


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
        case "arm":
            _delay_ms_arm(ms)

@inline
def _delay_ms_pic14(ms: uint8):
    """Software millisecond delay loop for PIC14 architecture."""
    # PIC14: Tcy = Fosc/4. Nested loop costs outer*(3*inner + 4) + 2 Tcy;
    # with inner=165 each outer turn is ~499 Tcy, so outer = Tcy_per_ms/500.
    # match __FREQ__ is dead-code-eliminated at compile time -- only the
    # matching branch survives in the assembled output.
    i: uint8 = 0
    while i < ms:
        match __FREQ__:
            case 4_000_000:
                # 2 * 499 = 998 Tcy ~ 1 ms
                asm("    MOVLW 0x02")
                asm("    MOVWF __dly_c2")
                asm("_dly_o4m:")
                asm("    MOVLW 0xA5")
                asm("    MOVWF __dly_c1")
                asm("_dly_i4m:")
                asm("    DECFSZ __dly_c1, F")
                asm("    GOTO _dly_i4m")
                asm("    DECFSZ __dly_c2, F")
                asm("    GOTO _dly_o4m")
            case 8_000_000:
                # 4 * 499 = 1996 Tcy ~ 1 ms
                asm("    MOVLW 0x04")
                asm("    MOVWF __dly_c2")
                asm("_dly_o8m:")
                asm("    MOVLW 0xA5")
                asm("    MOVWF __dly_c1")
                asm("_dly_i8m:")
                asm("    DECFSZ __dly_c1, F")
                asm("    GOTO _dly_i8m")
                asm("    DECFSZ __dly_c2, F")
                asm("    GOTO _dly_o8m")
            case 20_000_000:
                # 10 * 499 = 4990 Tcy ~ 1 ms
                asm("    MOVLW 0x0A")
                asm("    MOVWF __dly_c2")
                asm("_dly_o20m:")
                asm("    MOVLW 0xA5")
                asm("    MOVWF __dly_c1")
                asm("_dly_i20m:")
                asm("    DECFSZ __dly_c1, F")
                asm("    GOTO _dly_i20m")
                asm("    DECFSZ __dly_c2, F")
                asm("    GOTO _dly_o20m")
            case _:
                # 16 MHz table (also the fallback): 8 * 499 = 3992 Tcy ~ 1 ms
                asm("    MOVLW 0x08")
                asm("    MOVWF __dly_c2")
                asm("_dly_o16m:")
                asm("    MOVLW 0xA5")
                asm("    MOVWF __dly_c1")
                asm("_dly_i16m:")
                asm("    DECFSZ __dly_c1, F")
                asm("    GOTO _dly_i16m")
                asm("    DECFSZ __dly_c2, F")
                asm("    GOTO _dly_o16m")
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
        match __FREQ__:
            case 4_000_000:
                asm("    MOVLW 0x03")
                asm("    MOVWF __dly_c2, ACCESS")
                asm("_dly_o4m18:")
                asm("    MOVLW 0x6E")
                asm("    MOVWF __dly_c1, ACCESS")
                asm("_dly_i4m18:")
                asm("    DECFSZ __dly_c1, F, ACCESS")
                asm("    BRA _dly_i4m18")
                asm("    DECFSZ __dly_c2, F, ACCESS")
                asm("    BRA _dly_o4m18")
            case 8_000_000:
                asm("    MOVLW 0x03")
                asm("    MOVWF __dly_c2, ACCESS")
                asm("_dly_o8m18:")
                asm("    MOVLW 0xDD")
                asm("    MOVWF __dly_c1, ACCESS")
                asm("_dly_i8m18:")
                asm("    DECFSZ __dly_c1, F, ACCESS")
                asm("    BRA _dly_i8m18")
                asm("    DECFSZ __dly_c2, F, ACCESS")
                asm("    BRA _dly_o8m18")
            case 16_000_000:
                asm("    MOVLW 0x06")
                asm("    MOVWF __dly_c2, ACCESS")
                asm("_dly_o16m18:")
                asm("    MOVLW 0xDD")
                asm("    MOVWF __dly_c1, ACCESS")
                asm("_dly_i16m18:")
                asm("    DECFSZ __dly_c1, F, ACCESS")
                asm("    BRA _dly_i16m18")
                asm("    DECFSZ __dly_c2, F, ACCESS")
                asm("    BRA _dly_o16m18")
            case _:
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

def _delay_1ms_riscv():
    # RISC-V: ADDI+BNE = ~3-4 cycles/iter depending on pipeline.
    # CH32V003 at 48MHz: 1ms = 48000 cycles.
    # Nested: 63 x 255 x 3 = 48195 ~ 1ms
    # Deliberately not @inline: the loop labels below are fixed names, so the
    # body must be emitted exactly once no matter how many delay_ms calls the
    # program makes. t0/t1 are caller-saved, so there is nothing to preserve.
    asm("    LI t0, 63")
    asm("_dly_outer_rv:")
    asm("    LI t1, 255")
    asm("_dly_inner_rv:")
    asm("    ADDI t1, t1, -1")
    asm("    BNEZ t1, _dly_inner_rv")
    asm("    ADDI t0, t0, -1")
    asm("    BNEZ t0, _dly_outer_rv")

@inline
def _delay_ms_riscv(ms: uint8):
    """Software millisecond delay loop for RISC-V architecture."""
    i: uint8 = 0
    while i < ms:
        _delay_1ms_riscv()
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
@inline
def _delay_ms_arm(ms: uint16):
    # ARM Cortex-M family: dispatch to the per-chip timer-poll delay. The
    # non-matching branch is dead-code-eliminated (compile-time __CHIP__.name).
    if __CHIP__.name == "rp2040":
        _delay_ms_rp2040(ms)
    elif __CHIP__.name == "rp2350":
        _delay_ms_rp2350(ms)


def _delay_ms_rp2040(ms: uint16):
    # Poll the hardware microsecond timer instead of a calibrated busy-loop.
    # 1 ms = 1000 us; the timer runs at 1 MHz, so the delay is exact on real
    # silicon and on the emulator (whose timer advances by elapsed cycles),
    # independent of CPU clock and pipeline timing. ms * 1000 fits in uint32
    # for the full uint16 range (65535 ms -> 65_535_000 us).
    _delay_us_rp2040(ms * 1000)


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
        case "arm":
            _delay_us_arm(us)

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

def _delay_us_avr(us: uint8):
    """Software microsecond delay loop for AVR architecture.

    NON-inline on purpose: the 12-NOP loop body is emitted once as a shared
    subroutine instead of being duplicated into flash at every delay_us() call
    site (mirrors _delay_ms_avr). A delay-heavy driver like the HD44780 LCD calls
    delay_us dozens of times; inlining the body cost ~24 bytes of NOPs per call.
    The fixed CALL/RET overhead (~9 cycles, <0.6 us) is within the documented
    <0.1%-at-typical-counts accuracy budget."""
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
def _delay_us_arm(us: uint32):
    # ARM Cortex-M family: dispatch to the per-chip timer-poll delay.
    if __CHIP__.name == "rp2040":
        _delay_us_rp2040(us)
    elif __CHIP__.name == "rp2350":
        _delay_us_rp2350(us)


@inline
def _delay_us_rp2040(us: uint32):
    """Microsecond delay for RP2040, driven by the hardware TIMER (1 MHz)."""
    # TIMER.TIMERAWL (0x40054028) is the raw low 32 bits of the free-running
    # microsecond counter, readable with no latching side effect. The volatile
    # MMIO load in the loop condition is a real side effect, so opt -O2 cannot
    # delete the wait; no asm("nop") barrier is needed. uint32 subtraction
    # wraps modulo 2**32, so delays up to ~71 minutes are correct across the
    # counter roll-over.
    timer: ptr[uint32] = ptr(0x40054028)
    start: uint32 = timer.value
    while (timer.value - start) < us:
        pass


def _delay_ms_rp2350(ms: uint16):
    # See _delay_ms_rp2040; the RP2350 microsecond TIMER0 lives at a different base.
    _delay_us_rp2350(ms * 1000)


@inline
def _delay_us_rp2350(us: uint32):
    """Microsecond delay for RP2350, driven by the hardware TIMER0 (1 MHz)."""
    # TIMER0.TIMERAWL (0x400B0028) is the raw low 32 bits of the free-running
    # microsecond counter (RP2350 moved TIMER to base 0x400B0000). The volatile
    # MMIO load in the loop condition is a real side effect, so opt -O2 cannot
    # delete the wait; uint32 subtraction wraps modulo 2**32.
    timer: ptr[uint32] = ptr(0x400B0028)
    start: uint32 = timer.value
    while (timer.value - start) < us:
        pass


@inline
def millis_init():
    """Initialize the hardware millisecond counter.

    Configures Timer0 (prescaler 64) and registers an overflow ISR that
    increments a uint32 counter once per ~1 ms.  Must be called once before
    using millis() or micros().  AVR (ATmega328P) only.
    """
    match __CHIP__.arch:
        case "avr":
            from pymcu.hal.avr.timer.atmega328p import millis_init as _millis_init_avr
            _millis_init_avr()
        case "pic18":
            from pymcu.hal.timer import millis_init as _millis_init_pic18
            _millis_init_pic18()


@inline
def millis() -> uint32:
    """Return elapsed milliseconds since the timebase started.

    avr (ATmega): Timer0-overflow counter, read atomically under CLI/SEI;
      requires millis_init() first.
    rp2040/rp2350: the hardware microsecond TIMER, divided down.
    Everything else has no timebase, and a counter frozen at 0 turns every
    `millis() - last > interval` into a test that never fires, with nothing
    to tell the user why -- so those targets raise here instead.
    """
    if __CHIP__.name == "rp2040" or __CHIP__.name == "rp2350":
        return micros() // 1000
    elif __CHIP__.arch == "avr":
        from pymcu.hal.timer import millis as _millis_avr
        return _millis_avr()
    elif __CHIP__.name == "pic18f45k50":
        from pymcu.hal.timer import millis as _millis_pic18
        return _millis_pic18()
    else:
        raise CompileError("millis() needs a timebase; not available on this architecture yet: only ATmega AVR (Timer0), PIC18F45K50 (Timer0) and RP2040/RP2350 (hardware TIMER) have one, so it would be frozen at 0 and every elapsed-time test would silently never fire. Use pymcu.time.delay_ms() to pace a loop instead.")


@inline
def micros() -> uint32:
    """Return elapsed microseconds since the timebase started.

    avr (ATmega): the overflow counter combined with the current TCNT0, for
      4 us resolution at 16 MHz / prescaler 64; requires millis_init() first.
    rp2040/rp2350: TIMERAWL, the raw low 32 bits of the free-running 1 MHz
      counter -- exact microseconds, and the same counter asyncio reads.
    Everything else raises, for the reason given in millis().
    """
    if __CHIP__.name == "rp2040":
        t: ptr[uint32] = ptr(0x40054028)
        return t.value
    elif __CHIP__.name == "rp2350":
        t2: ptr[uint32] = ptr(0x400B0028)
        return t2.value
    elif __CHIP__.arch == "avr":
        from pymcu.hal.timer import micros as _micros_avr
        return _micros_avr()
    elif __CHIP__.name == "pic18f45k50":
        from pymcu.hal.timer import micros as _micros_pic18
        return _micros_pic18()
    else:
        raise CompileError("micros() needs a timebase; not available on this architecture yet: only ATmega AVR (Timer0), PIC18F45K50 (Timer0) and RP2040/RP2350 (hardware TIMER) have one, so it would be frozen at 0 and every elapsed-time test would silently never fire. Use pymcu.time.delay_us() to pace a loop instead.")

