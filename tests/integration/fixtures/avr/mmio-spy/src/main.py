# PyMCU -- mmio-spy: MMIO register writes captured by hooks in the test suite
#
# The firmware configures Timer0 (writes to TCCR0A, OCR0A, TCCR0B in that order)
# and toggles PB5 three times. Tests register Cpu.Mmio.RegisterWrite() hooks
# BEFORE running to capture: written values, write counts, and relative order.
#
# Data-space addresses (ATmega328P):
#   TCCR0A = 0x44    TCCR0B = 0x45    OCR0A = 0x47    PORTB = 0x25
#
# Intentional write order: TCCR0A first, then OCR0A, then TCCR0B.
# This reflects good practice: set mode before enabling the prescaler clock.
#
from pymcu.chips.atmega328p import PORTB, DDRB, TCCR0A, TCCR0B, OCR0A
from pymcu.types import asm


def main():
    DDRB[5] = 1

    # Configure Timer0 Fast PWM in the correct order:
    # 1. Set mode register first (before enabling the clock)
    TCCR0A.value = 0x83   # COM0A1=1, WGM01=1, WGM00=1
    # 2. Set compare value
    OCR0A.value  = 128    # 50% duty cycle (0x80)
    # 3. Enable clock last to avoid glitches
    TCCR0B.value = 0x01   # CS00: no prescaler

    # Toggle PB5 three times: high -> low -> high
    PORTB[5] = 1
    PORTB[5] = 0
    PORTB[5] = 1

    asm("BREAK")
    while True:
        pass
