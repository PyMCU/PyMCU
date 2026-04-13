# PyMCU -- cycle-timing: elapsed cycle count verification via BREAK checkpoints
#
# Uses two BREAK checkpoints bracketing a delay_ms(1) call to let the test
# measure the elapsed simulated cycle count and verify it falls in the expected
# range for a 1ms software delay at 16 MHz (~16 000 cycles).
#
# The _delay_1ms_avr() helper runs:
#   21 outer * 255 inner * 3 cycles/iter = 16 065 loop cycles
#   plus ~80 cycles overhead (PUSH/POP, CALL/RET, loop setup)
# -> expected total ~16 000 to 17 000 cycles for delay_ms(1).
#
# The test asserts the delta is in [14 000, 20 000] -- a generous window that
# catches both "delay too short" and "delay too long" regressions without being
# brittle to minor loop-count adjustments.
#
# Checkpoints:
#   1 -- immediately before delay_ms(1)
#   2 -- immediately after  delay_ms(1)
#
from pymcu.time import delay_ms
from pymcu.types import asm


def main():
    asm("BREAK")    # Checkpoint 1: record cycle counter here

    delay_ms(1)     # ~16 000 cycles at 16 MHz

    asm("BREAK")    # Checkpoint 2: measure elapsed cycles

    while True:
        pass
