# PyMCU -- memory-layout: hardware register memory layout verification
#
# Configures Timer0 and Timer1 with known values, then halts at BREAK.
# Tests use Memory.Should().HaveByteAt(), HaveWordAt(), and HaveBytesAt()
# from the TestKit to verify values at exact data-space addresses.
#
# Data-space addresses (ATmega328P):
#   TCCR0A = 0x44    TCCR0B = 0x45    OCR0A  = 0x47
#   TCCR1A = 0x80    TCCR1B = 0x81
#   ICR1   = 0x86  (ptr[uint16]: ICR1L=0x86, ICR1H=0x87, little-endian)
#   OCR1A  = 0x88  (ptr[uint16]: OCR1AL=0x88, OCR1AH=0x89, little-endian)
#
# workaround: multi-line imports with parentheses not supported by the parser
from pymcu.chips.atmega328p import TCCR0A, TCCR0B, OCR0A
from pymcu.chips.atmega328p import TCCR1A, TCCR1B
from pymcu.chips.atmega328p import ICR1, OCR1A
from pymcu.types import asm


def main():
    # Timer0: Fast PWM mode (COM0A1=1, WGM01=1, WGM00=1 -> 0x83), prescaler 1 (CS00=1)
    TCCR0A.value = 0x83   # 0b10000011
    TCCR0B.value = 0x01   # CS00: no prescaler
    OCR0A.value  = 200    # 0xC8: duty cycle value

    # Timer1: CTC mode (WGM12=1 -> bit3), prescaler 8 (CS11=1 -> bit1) -> 0x0A
    TCCR1A.value = 0x00   # normal compare output, no PWM
    TCCR1B.value = 0x0A   # WGM12=1, CS11=1

    # ICR1 and OCR1A written as 16-bit registers (ptr[uint16] in atmega328p.py)
    ICR1.value  = 16000   # 0x3E80: 1 ms period at 16 MHz / prescaler 1
    OCR1A.value = 8000    # 0x1F40: 50% duty cycle

    asm("BREAK")
    while True:
        pass
