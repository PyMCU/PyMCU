# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# Servo HAL for ATmega328P using Timer1 Fast PWM (mode 14: TOP=ICR1)
#
# Period: 20 ms (50 Hz)  ->  ICR1 = 19999  (F_CPU=16MHz, prescaler=8)
#   clock tick = 0.5 us  ->  1 us = 2 ticks
#
# Pulse range:
#   1000 us (0°)   -> OCR1x = 1999
#   2000 us (180°) -> OCR1x = 3999
#
# Channel A: OC1A = PB1 (Arduino D9)
# Channel B: OC1B = PB2 (Arduino D10)
#
# TCCR1A = COM1A1(7)+COM1B1(5)+WGM11(1) = 0b10100010 = 0xA2  (non-inverting fast PWM)
# TCCR1B = WGM13(4)+WGM12(3)+CS11(1)   = 0b00011010 = 0x1A  (prescaler 8, mode 14)
#
# Conflict: uses Timer1 exclusively.  Do NOT mix with:
#   - PWM on OC1A (D9) or OC1B (D10)
#   - Any Timer(1, ...) instance
#   - tone() on 32U4 (which also uses Timer1)

from pymcu.chips.atmega328p import TCCR1A, TCCR1B, ICR1L, ICR1H, ICR1
from pymcu.chips.atmega328p import OCR1AL, OCR1AH, OCR1A
from pymcu.chips.atmega328p import OCR1BL, OCR1BH, OCR1B
from pymcu.chips.atmega328p import DDRB, PORTB
from pymcu.types import uint8, uint16, inline, ptr

# 50 Hz @ 16 MHz, prescaler 8: TOP = (16_000_000 / (8 * 50)) - 1 = 39999
# But 39999 > 65535? No: 39999 fits in 16 bits fine.
# 20 ms period: 16_000_000 / 8 = 2_000_000 ticks/s; 2_000_000 / 50 = 40000 ticks; TOP = 39999
# 1 tick = 0.5 us
# 1000 us -> 2000 ticks -> OCR = 2000 - 1 = 1999
# 2000 us -> 4000 ticks -> OCR = 4000 - 1 = 3999
_SERVO_TOP:  uint16 = 39999
_PULSE_MIN:  uint16 = 1999    # 1000 us = 0 degrees
_PULSE_MAX:  uint16 = 3999    # 2000 us = 180 degrees


@inline
def _write16(hi_reg: ptr[uint8], lo_reg: ptr[uint8], val: uint16):
    # AVR 16-bit write: HIGH byte first (into TEMP), then LOW byte triggers update.
    hi: uint8 = uint8(val >> 8)
    hi_reg.value = hi
    lo: uint8 = uint8(val)
    lo_reg.value = lo


@inline
def servo_init():
    """Configure Timer1 for dual-channel 50 Hz Servo PWM.

    Must be called once before writing positions.  Both channels (D9 and D10)
    are enabled simultaneously -- the hardware only supports one prescaler.
    """
    DDRB[1] = 1     # OC1A (D9) as output
    DDRB[2] = 1     # OC1B (D10) as output

    # Stop timer before reconfiguring to avoid glitch.
    TCCR1B.value = 0x00

    # ICR1 = TOP = 39999 (20 ms period @ 16 MHz, prescaler 8)
    _write16(ICR1H, ICR1L, _SERVO_TOP)

    # Set both channels to minimum pulse (1000 us = 0 degrees) so the servo
    # doesn't jump when the timer starts.
    _write16(OCR1AH, OCR1AL, _PULSE_MIN)
    _write16(OCR1BH, OCR1BL, _PULSE_MIN)

    # Fast PWM mode 14 (TOP=ICR1), non-inverting output on OC1A and OC1B:
    # TCCR1A = COM1A1 | COM1B1 | WGM11 = 0xA2
    TCCR1A.value = 0xA2

    # WGM13 | WGM12 | CS11 (prescaler 8) = 0x1A
    TCCR1B.value = 0x1A


@inline
def servo_write_a(degrees: uint8):
    """Write servo position on channel A (OC1A / Arduino D9).

    degrees: 0 (1000 us) to 180 (2000 us).
    """
    d: uint16 = degrees
    pulse: uint16 = _PULSE_MIN + (d * 100) // 9   # 11.111 ticks/degree, exact at 90 and 180
    _write16(OCR1AH, OCR1AL, pulse)


@inline
def servo_write_b(degrees: uint8):
    """Write servo position on channel B (OC1B / Arduino D10)."""
    d: uint16 = degrees
    pulse: uint16 = _PULSE_MIN + (d * 100) // 9
    _write16(OCR1BH, OCR1BL, pulse)


@inline
def servo_write_us_a(us: uint16):
    """Write pulse width in microseconds on channel A (D9).

    us: 1000 (0°) to 2000 (180°).  Values outside this range are clamped
    by the hardware (OCR1A wraps silently if > TOP).
    """
    ticks: uint16 = us * 2 - 1   # 0.5 us per tick -> ticks = us*2; TOP offset: -1
    _write16(OCR1AH, OCR1AL, ticks)


@inline
def servo_write_us_b(us: uint16):
    """Write pulse width in microseconds on channel B (D10)."""
    ticks: uint16 = us * 2 - 1
    _write16(OCR1BH, OCR1BL, ticks)


@inline
def servo_stop():
    """Detach both servo channels and stop Timer1."""
    TCCR1B.value = 0x00    # stop timer
    TCCR1A.value = 0x00    # disconnect OC1A/OC1B
    PORTB[1] = 0           # drive D9 low
    PORTB[2] = 0           # drive D10 low
