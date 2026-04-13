# PyMCU -- isr-sp-preservation: stack pointer integrity across ISR entry/exit
#
# Verifies that the AVR context save/restore sequence emitted by the compiler
# (PUSH R16/R17/R18/SREG on entry, POP on exit) leaves SP exactly where it
# was before the interrupt fired.
#
# Strategy:
#   1. Hit BREAK before enabling interrupts so the test can record SP baseline.
#   2. Enable Timer0 overflow (prescaler=1 so it fires quickly) and SEI.
#   3. Spin until ISR flag is set (ISR has fired AND returned via RETI).
#   4. Hit BREAK; the test asserts Cpu.SP == baseline captured at step 1.
#
# The ISR only sets a single flag bit using SBI (no extra stack usage), so any
# SP corruption between the two BREAKs must come from a bad context save/restore.
#
# Data-space addresses:
#   GPIOR0 = 0x3E   (flag byte: bit 0 set by ISR)
#
# Timer0 OVF vector: 0x0020 (byte address on ATmega328P)
#
from pymcu.types import uint8, interrupt, asm
from pymcu.chips.atmega328p import TCCR0B, TIMSK0, GPIOR0


@interrupt(0x0020)
def timer0_ovf_isr():
    # Set flag bit 0 -- SBI instruction, no stack frame pushed
    GPIOR0[0] = 1


def main():
    GPIOR0[0] = 0           # clear ISR flag before enabling timer

    asm("BREAK")            # Checkpoint 1: baseline SP (interrupts disabled)

    # Enable Timer0 overflow with prescaler 1 (CS00=1) so it fires in ~256 cycles
    TCCR0B.value = 1
    TIMSK0[0] = 1           # enable TOIE0
    asm("SEI")              # enable global interrupts

    # Spin until ISR fires and sets GPIOR0[0]; after this loop the ISR has
    # completed its full RETI (including context restore)
    while GPIOR0[0] == 0:
        pass

    asm("BREAK")            # Checkpoint 2: SP must equal baseline from checkpoint 1

    while True:
        pass
