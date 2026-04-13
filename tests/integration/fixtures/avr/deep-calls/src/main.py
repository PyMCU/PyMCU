# PyMCU -- deep-calls: callee-saved register preservation across 3-level calls
#
# Verifies that local variables in outer stack frames are not corrupted when
# deep non-inline function calls allocate their own locals in the same
# physical registers (R4-R15 range in PyMCU's greedy allocator).
#
# Call chain:
#   main() -> middle(1) -> inner(1)
#
# Each level assigns a magic byte to a local variable BEFORE calling the next
# level, then stores that local to a GPIOR register AFTER the callee returns.
# If any register is clobbered, the stored value will differ from the magic byte.
#
# Expected values:
#   GPIOR0 = 0xAA  (main's local, must survive call to middle)
#   GPIOR1 = 0xBB  (middle's local, must survive call to inner)
#   GPIOR2 = 0x88  (computed: inner returns 1+0xCC=0xCD; middle adds 0xBB:
#                   0xCD + 0xBB = 0x188 -> 0x88 mod 256)
#
# Data-space addresses:
#   GPIOR0 = 0x3E   GPIOR1 = 0x4A   GPIOR2 = 0x4B
#
from pymcu.types import uint8, asm
from pymcu.chips.atmega328p import GPIOR0, GPIOR1, GPIOR2


def inner(x: uint8) -> uint8:
    local_inner: uint8 = 0xCC
    return x + local_inner      # 1 + 0xCC = 0xCD


def middle(x: uint8) -> uint8:
    local_middle: uint8 = 0xBB
    result: uint8 = inner(x)
    # After inner() returns, local_middle must still hold 0xBB.
    # If inner() clobbered the register holding local_middle this will be wrong.
    GPIOR1.value = local_middle
    return result + local_middle  # 0xCD + 0xBB = 0x188 -> 0x88


def main():
    local_main: uint8 = 0xAA
    result: uint8 = middle(1)
    # After middle() returns (which itself called inner()), local_main must
    # still hold 0xAA.
    GPIOR0.value = local_main
    GPIOR2.value = result       # expected 0x88
    asm("BREAK")

    while True:
        pass
