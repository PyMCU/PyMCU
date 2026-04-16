# PyMCU -- isr-reg-preservation: R24/R25/R19 are preserved across ISR entry/exit.
#
# The bug was: EmitContextSave only pushed R16/R17/R18 + SREG.
# The codegen uses R19 (CPC in 16-bit comparisons), R24/R25 (all arithmetic
# results), and R26/R27 (X pointer for LoadIndirect/StoreIndirect).
# An ISR that internally uses those registers would corrupt the values
# that the interrupted main() had in those logical variables.
#
# Strategy:
#   1. main() establishes a known uint16 value: sentinel = 0xBEEF.
#   2. BREAK (checkpoint 1) -- record sentinel in GPIOR0/GPIOR1.
#   3. Enable Timer0 OVF ISR (prescaler=1). The ISR performs a uint16 add
#      internally (forces R24/R25/R19 use), then sets a flag bit.
#   4. Spin until flag is set.
#   5. Re-read the sentinel variable into GPIOR0/GPIOR1.
#   6. BREAK (checkpoint 2) -- GPIOR0/GPIOR1 must still be 0xEF/0xBE.
#
# If R24/R25 are not saved/restored by the ISR context save, the sentinel
# value will be corrupted after the ISR returns.
#
# Data-space addresses (ATmega328P):
#   GPIOR0 = 0x3E, GPIOR1 = 0x4A, GPIOR2 = 0x4B
#   TCCR0B = 0x45, TIMSK0 = 0x6E
#
from pymcu.types import uint8, uint16, interrupt, asm
from pymcu.chips.atmega328p import GPIOR0, GPIOR1, GPIOR2, TCCR0B, TIMSK0


@interrupt(0x0020)
def timer0_ovf_isr():
    # Force use of R24/R25 and R19 (16-bit addition inside ISR body)
    x: uint16 = 0x0100
    y: uint16 = x + 0x0001
    GPIOR2.value = uint8(y & 0xFF)
    # Signal that ISR ran
    GPIOR0[7] = 1


def main():
    sentinel: uint16 = 0xBEEF

    # Store sentinel before enabling ISR
    GPIOR0.value = uint8(sentinel & 0xFF)
    GPIOR1.value = uint8((sentinel >> 8) & 0xFF)
    asm("BREAK")  # Checkpoint 1

    GPIOR0[7] = 0
    TCCR0B.value = 1   # prescaler=1
    TIMSK0[0] = 1      # TOIE0
    asm("SEI")

    # Spin until ISR fires
    while GPIOR0[7] == 0:
        pass

    # Re-read sentinel; must still be 0xBEEF
    GPIOR0.value = uint8(sentinel & 0xFF)
    GPIOR1.value = uint8((sentinel >> 8) & 0xFF)
    asm("BREAK")  # Checkpoint 2

    while True:
        pass

