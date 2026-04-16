# PyMCU -- isr-reg-preservation: R24/R25/R19 are preserved across ISR entry/exit.
#
# Bug: EmitContextSave only pushed R16/R17/R18 + SREG.
# The codegen uses R19 (CPC in 16-bit comparisons), R24/R25 (all arithmetic).
# An ISR that internally uses those registers corrupts main() context.
#
# Strategy:
#   1. main() sets a sentinel uint16 = 0xBEEF, stores it in GPIOR0/GPIOR1.
#   2. BREAK (checkpoint 1).
#   3. Enable Timer0 OVF. The ISR does a uint16 add (forces R24/R25 use).
#   4. Spin until ISR flag is set.
#   5. Re-store sentinel into GPIOR0/GPIOR1.
#   6. BREAK (checkpoint 2). GPIOR0/GPIOR1 must still be 0xEF/0xBE.
#
# Data-space addresses (ATmega328P):
#   GPIOR0 = 0x3E, GPIOR1 = 0x4A, GPIOR2 = 0x4B
#   TCCR0B = 0x45, TIMSK0 = 0x6E
#
from pymcu.types import uint8, uint16, interrupt, asm
from pymcu.chips.atmega328p import GPIOR0, GPIOR1, GPIOR2, TCCR0B, TIMSK0


def split_lo(v: uint16) -> uint8:
    return v & 0xFF


def split_hi(v: uint16) -> uint8:
    return (v >> 8) & 0xFF


@interrupt(0x0020)
def timer0_ovf_isr():
    # Force use of R24/R25 (uint16 arithmetic inside ISR)
    x: uint16 = 0x0100
    y: uint16 = x + 0x0001
    GPIOR2.value = split_lo(y)
    # Signal ISR ran via bit 7
    GPIOR0[7] = 1


def main():
    sentinel: uint16 = 0xBEEF

    GPIOR0.value = split_lo(sentinel)
    GPIOR1.value = split_hi(sentinel)
    asm("BREAK")  # Checkpoint 1: before ISR

    GPIOR0[7] = 0
    TCCR0B.value = 1   # prescaler=1
    TIMSK0[0] = 1      # TOIE0
    asm("SEI")

    while GPIOR0[7] == 0:
        pass

    # Re-read the sentinel variable (must still hold 0xBEEF in registers)
    GPIOR0.value = split_lo(sentinel)
    GPIOR1.value = split_hi(sentinel)
    asm("BREAK")  # Checkpoint 2: after ISR

    while True:
        pass

