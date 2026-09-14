# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# Pulse capture and pulse trains -- ATmega48/88/168/328 family
#
# Two halves, and they use different timers on purpose so a program can do both at
# once (an infrared remote reads on one pin and sends on another).
#
# CAPTURE. Timer1 runs free in normal mode at prescaler 8, and a pin-change interrupt
# on the measured pin timestamps every edge. The length of a pulse is the difference
# between two timestamps.
#
#   Why not Timer1's own input capture unit, which is what it is for? ICP1 is PB0 and
#   nothing else on this part. A sensor is wired where the board has room, and the two
#   acceptance cases here -- a DHT on any digital pin and an infrared receiver on any
#   digital pin -- are both wired somewhere other than D8 in every guide that exists.
#   A pin-change interrupt costs a few cycles of latency and works on all 23 pins, and
#   the latency is common to both edges of a pulse, so it cancels in the difference.
#
#   Prescaler 8 is 0.5 us per tick at 16 MHz: fine enough for a DHT's 26 us and 70 us
#   bits, and the 16-bit counter still spans 32.7 ms, which covers an NEC leader (9 ms)
#   and any gap inside a frame. Prescaler 64 would have spanned 262 ms and quantised
#   the DHT's two bit lengths to 6 and 17 counts, which is a reading that works until
#   the sensor is cold.
#
# TRAIN. Timer2 in fast PWM mode 7 (TOP = OCR2A) generates the carrier on OC2B (PD3),
# and the pulse list gates it by connecting and disconnecting the compare output. Mode
# 7 is the only mode on this part that reaches an arbitrary carrier frequency: the
# fixed-TOP modes give 62500, 7812, 1953, 976, 488, 244 and 61 Hz and nothing between,
# and 38 kHz is not one of them.
# -----------------------------------------------------------------------------
from pymcu.chips import __FREQ__
from pymcu.chips.atmega328p import (
    TCCR1A, TCCR1B, TCNT1,
    TCCR2A, TCCR2B, OCR2A, OCR2B, TCNT2,
    DDRD, PORTD, SREG,
)
from pymcu.exceptions import CompileError
from pymcu.types import uint8, uint16, uint32, inline, const, claim, compile_isr
from pymcu.time import delay_us
from pymcu.hal.avr.gpio.atmega328p import pin_irq_enable

# The capture ring. Its size is fixed at compile time because a global array is, so a
# PulseCapture asking for more than this is refused rather than quietly given less.
# 128 entries is 256 bytes of SRAM, and it costs nothing in a program that captures no
# pulses: nothing references the array and it is eliminated. It covers the two shapes
# that matter -- a DHT frame is 81 pulses and an NEC frame is 67.
PULSE_CAPACITY = 128

_pulse_buf:   uint16[128] = [0] * 128
_pulse_head:  uint8  = 0
_pulse_tail:  uint8  = 0
_pulse_len:   uint8  = 0
_pulse_last:  uint16 = 0
_pulse_armed: uint8  = 0
_pulse_paused: uint8 = 0
_pulse_maxlen: uint8 = 128


# Timer1 ticks per microsecond at prescaler 8. Two at 16 MHz, one at 8 MHz. A compile-time
# constant, so the division in the ISR folds to a shift.
@inline
def _pulse_ticks_per_us() -> uint8:
    return uint8(__FREQ__ // 8 // 1000000)


# The pin-change ISR. NOT @inline: the vector jumps here, so it needs an address.
#
# Every edge is one subtraction and one store. The first edge after a clear only sets the
# reference, because the interval before it started before anyone was watching: that is
# also what makes idle_state unnecessary here, since the first edge a line at rest produces
# is the one that leaves rest, and the first stored interval is therefore the first pulse
# away from idle, for either value of idle_state.
def pulse_isr():
    global _pulse_buf, _pulse_head, _pulse_len, _pulse_last, _pulse_armed, _pulse_paused
    global _pulse_maxlen
    now: uint16 = TCNT1.value
    if _pulse_paused:
        return
    if _pulse_armed == 0:
        _pulse_armed = 1
        _pulse_last = now
        return
    delta: uint16 = now - _pulse_last
    _pulse_last = now
    if _pulse_len < _pulse_maxlen:
        _pulse_buf[_pulse_head] = delta // _pulse_ticks_per_us()
        _pulse_head = (_pulse_head + 1) & 0x7F
        _pulse_len = _pulse_len + 1


# Start the time base the ISR reads. Timer1 normal mode, prescaler 8, nothing else touched.
#
# The claim is what stops a PWM or a servo from taking Timer1 out from under this: both
# reprogram its prescaler, and a capture whose clock changed reports lengths that are wrong
# by that ratio, silently. 20 is a value no PWM prescaler code takes, so any PWM on PB1 or
# PB2 in the same program is refused where it is written.
@inline
def pulse_timebase_init():
    claim("Timer1 prescaler (PB1 and PB2 share it)", 20, "pulse timing",
          "Pulses are timed against Timer1 running free at prescaler 8 -- capture timestamps "
          "its edges there and a train measures its gaps there -- and a PWM or a servo on "
          "PB1/PB2 reprograms that prescaler. Move the PWM to PD5/PD6 (Timer0) or PB3/PD3 "
          "(Timer2), or drop the pulse work")
    TCCR1A.value = 0x00
    TCCR1B.value = 0x02   # CS1[2:0] = 010: prescaler 8
    TCNT1.value = 0


@inline
def pulse_capture_timebase_init():
    pulse_timebase_init()


# Put the pin's edges on the ISR.
#
# The registers and the handler go on separately, and the compile_isr() call is HERE rather
# than inside Pin.irq(), because an inlined function reference is resolved in the module that
# defines the function it was passed to: handing pulse_isr to pin_irq_setup() makes the
# compiler look for that name in the GPIO module, where it does not exist (PyMCU#321).
@inline
def pulse_capture_attach(pin: const):
    pin_irq_enable(pin, 3)             # 3 = any edge
    match pin:
        case 'PD2' | 2:
            compile_isr(pulse_isr, 0x0002)
        case 'PD3' | 3:
            compile_isr(pulse_isr, 0x0004)
        case 'PB0' | 'PB1' | 'PB2' | 'PB3' | 'PB4' | 'PB5' | 8 | 9 | 10 | 11 | 12 | 13:
            compile_isr(pulse_isr, 0x0006)
        case 'PC0' | 'PC1' | 'PC2' | 'PC3' | 'PC4' | 'PC5' | 14 | 15 | 16 | 17 | 18 | 19:
            compile_isr(pulse_isr, 0x0008)
        case 'PD0' | 'PD1' | 'PD4' | 'PD5' | 'PD6' | 'PD7' | 0 | 1 | 4 | 5 | 6 | 7:
            compile_isr(pulse_isr, 0x000A)


@inline
def pulse_capture_set_maxlen(maxlen: const[uint16]):
    global _pulse_maxlen
    if maxlen > 128:
        raise CompileError(
            "this pulse capture can hold 128 pulses and cannot be sized per program: the "
            "buffer is a fixed array in the HAL, allocated at compile time. Ask for 128 or "
            "fewer, and read them out more often -- a pulse that arrives with the buffer "
            "full is dropped.")
    if maxlen == 0:
        raise CompileError(
            "a pulse capture with room for no pulses would record nothing. Ask for at least "
            "one, or 2 for a single high-low pair, which is what CircuitPython's PulseIn "
            "defaults to.")
    _pulse_maxlen = uint8(maxlen)


@inline
def pulse_capture_clear():
    global _pulse_head, _pulse_tail, _pulse_len, _pulse_armed
    asm_cli()
    _pulse_head = 0
    _pulse_tail = 0
    _pulse_len = 0
    _pulse_armed = 0
    asm_sei()


@inline
def pulse_capture_count() -> uint16:
    global _pulse_len
    return _pulse_len


@inline
def pulse_capture_maxlen() -> uint16:
    global _pulse_maxlen
    return _pulse_maxlen


@inline
def pulse_capture_capacity() -> uint16:
    return 128


# The oldest pulse, removed from the buffer. 0 when there is none, which is also what a
# zero-length pulse would read as; a caller that cares checks the count first.
def pulse_capture_popleft() -> uint16:
    global _pulse_buf, _pulse_tail, _pulse_len
    if _pulse_len == 0:
        return 0
    asm_cli()
    v: uint16 = _pulse_buf[_pulse_tail]
    _pulse_tail = (_pulse_tail + 1) & 0x7F
    _pulse_len = _pulse_len - 1
    asm_sei()
    return v


# The i-th oldest pulse, left in the buffer. Out of range reads as 0.
def pulse_capture_get(i: uint16) -> uint16:
    global _pulse_buf, _pulse_tail, _pulse_len
    if i >= _pulse_len:
        return 0
    return _pulse_buf[(_pulse_tail + uint8(i)) & 0x7F]


@inline
def pulse_capture_pause():
    global _pulse_paused
    _pulse_paused = 1


@inline
def pulse_capture_resume():
    global _pulse_paused, _pulse_armed
    _pulse_armed = 0
    _pulse_paused = 0


@inline
def pulse_capture_paused() -> uint8:
    global _pulse_paused
    return _pulse_paused


@inline
def asm_cli():
    SREG[7] = 0


@inline
def asm_sei():
    SREG[7] = 1


# --------------------------------------------------------------------------- #
# Pulse trains
# --------------------------------------------------------------------------- #

# The carrier period in Timer2 counts at prescaler 8: TOP = round(F_CPU / (8 * freq)) - 1.
# 38 kHz at 16 MHz is 52, which is 38 462 Hz -- 1.2 % high, inside the +/- 5 % every infrared
# receiver's band-pass allows.
@inline
def pulse_train_top(freq: const[uint32]) -> uint8:
    if __FREQ__ // (8 * freq) < 2:
        raise CompileError(
            "this carrier frequency is too high for Timer2 as this HAL programs it. With the "
            "prescaler at 8 the fastest carrier is about F_CPU / 16, which at 16 MHz is 1 MHz, "
            "and a usable one needs several counts per period. Infrared carriers are 30 kHz to "
            "56 kHz; ask for one of those.")
    if __FREQ__ // (8 * freq) > 256:
        raise CompileError(
            "this carrier frequency is too low for Timer2 as this HAL programs it. The period "
            "register holds 8 bits, so with the prescaler at 8 the slowest carrier is about "
            "F_CPU / 2048, which at 16 MHz is 7.8 kHz. Ask for a higher carrier, or gate a "
            "plain PWM with pymcu.hal.pwm yourself.")
    return uint8(__FREQ__ // (8 * freq) - 1)


@inline
def pulse_train_init(pin: const, freq: const[uint32], duty_u16: const[uint16]):
    match pin:
        case "PD3" | 3:
            pass
        case _:
            raise CompileError(
                "a pulse train's carrier comes out of OC2B, which is PD3 (D3 on an Arduino "
                "board) and nothing else on this part. Timer2's other channel, OC2A on PB3, "
                "cannot be used: mode 7 spends OCR2A on the carrier period, so PB3 has no "
                "compare value left. Move the emitter to D3.")
    pulse_timebase_init()
    claim("Timer2 prescaler (PB3 and PD3 share it)", 30, "PulseOut",
          "A pulse train runs Timer2 in fast PWM mode 7, where OCR2A is the carrier period, "
          "so the timer cannot also carry a plain PWM. Move the PWM to PD5/PD6 (Timer0) or "
          "PB1/PB2 (Timer1)")
    DDRD[3] = 1
    PORTD[3] = 0
    # Mode 7: WGM22 (TCCR2B bit 3) with WGM21:20 (TCCR2A bits 1:0) -- fast PWM, TOP = OCR2A.
    # The output stays DISCONNECTED here: a train that is not being sent must not emit a
    # carrier, and connecting it is exactly what send() does between the pulses.
    TCCR2A.value = 0x03
    TCCR2B.value = 0x0A   # WGM22 | CS2[2:0] = 010 (prescaler 8)
    OCR2A.value = pulse_train_top(freq)
    OCR2B.value = uint8((uint32(pulse_train_top(freq)) + 1) * duty_u16 // 65536)
    TCNT2.value = 0


@inline
def pulse_train_carrier_on():
    # COM2B1 = 1: connect OC2B, clearing on compare match and setting at BOTTOM.
    TCCR2A[5] = 1


@inline
def pulse_train_carrier_off():
    TCCR2A[5] = 0
    PORTD[3] = 0


@inline
def pulse_train_deinit():
    TCCR2A[5] = 0
    TCCR2A.value = 0x00
    TCCR2B.value = 0x00
    PORTD[3] = 0
    DDRD[3] = 0


# A microsecond wait that takes a 16-bit count, measured against the same Timer1 the capture
# timestamps edges with. pymcu.time.delay_us takes a uint8 and tops out at 255 us, which does
# not reach an NEC leader's 9000; stacking calls to it does reach one, and the loop around
# them costs real time -- measured, 560 us asked came out 598 and 1690 came out 1768, 7 % and
# 5 % long. A counter that is already running costs nothing to read and does not care how
# long the loop body is.
#
# A shared subroutine, not @inline: every pulse in a train would carry a copy.
#
# The counter is 16 bits at 2 ticks per microsecond, so a single wait tops out at 32 767 us;
# longer ones are taken in whole chunks of 30 000. An infrared frame's longest element is a
# 9 ms leader, well inside one chunk.
def pulse_delay_us(us: uint16):
    left: uint16 = us
    while left > 30000:
        _pulse_wait_ticks(30000 * _pulse_ticks_per_us())
        left = left - 30000
    _pulse_wait_ticks(left * _pulse_ticks_per_us())


# Getting here, reading the counter and coming back costs a fixed amount of time whatever the
# wait is, and it is the same for every pulse in a train, so it is subtracted once. Measured
# in avr8sharp at 16 MHz, watching the carrier gate itself: before the correction, 560 us
# asked held the carrier for 567.88 and 1690 for 1698.38, both about 7.9 us long, which is 16
# ticks at this prescaler. After it, 561.50, 561.38 and 1692.00 -- within 0.3 %.
#
# A wait shorter than the overhead cannot be shortened further, so anything under about 8 us
# takes about 8 us. Nothing an infrared or a one-wire protocol asks for is that short.
PULSE_WAIT_OVERHEAD_TICKS = 16


def _pulse_wait_ticks(ticks: uint16):
    if ticks <= 16:
        return
    target: uint16 = ticks - 16
    start: uint16 = TCNT1.value
    while (TCNT1.value - start) < target:
        pass
