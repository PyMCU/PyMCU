# PyMCU -- reg-pressure: forces register allocator overflow to Y+offset stack.
#
# AvrRegisterAllocator.cs allocates R4-R15 globally across ALL functions by
# use frequency (12 slots for 1-byte variables). This fixture uses 13 unique
# named variables across main() + helper(), forcing 1 to spill to _stackLayout
# (LDD Y+n / STD Y+n via StackAllocator.cs).
#
# make_val(n) is a non-inline passthrough: constants passed as arguments become
# runtime values from the callee's perspective, preventing the compiler from
# folding v0..v9 away via constant propagation.
#
# After the CALL to helper(), v0..v4 must still hold their correct values:
#   - If the spilled variable is read via LDD Y+n, its value must be intact.
#   - If a register-allocated variable was clobbered (bug), the sum fails.
#
# Checkpoint:
#   1 -- result=12, lo_sum=150 (v0+v1+v2+v3+v4 = 10+20+30+40+50)
#
# Data-space addresses:
#   GPIOR0 = 0x3E   GPIOR1 = 0x4A
from pymcu.chips.atmega328p import GPIOR0, GPIOR1, GPIOR2
from pymcu.types import uint8, asm


def make_val(n: uint8) -> uint8:
    """Non-inline passthrough; n arrives in R24 (runtime) from the callee's view."""
    return n


def helper(a: uint8, b: uint8) -> uint8:
    """2 named params + 1 named local = 3 named vars from helper's scope."""
    mid: uint8 = a + b
    return mid


def main():
    # 10 named vars; make_val() keeps them runtime so registers are actually used
    v0: uint8 = make_val(10)
    v1: uint8 = make_val(20)
    v2: uint8 = make_val(30)
    v3: uint8 = make_val(40)
    v4: uint8 = make_val(50)
    v5: uint8 = make_val(60)
    v6: uint8 = make_val(70)
    v7: uint8 = make_val(80)
    v8: uint8 = make_val(90)
    v9: uint8 = make_val(100)

    # CALL to helper: 3 more named vars -> total 13 > 12 -> 1 spills to Y+offset
    result: uint8 = helper(5, 7)   # = 12

    # All v0..v4 must survive the CALL unchanged
    lo_sum: uint8 = v0 + v1 + v2 + v3 + v4   # 10+20+30+40+50 = 150

    # v5..v9 verified via hi_sum
    hi_sum: uint8 = v5 + v6 + v7 + v8 + v9   # 60+70+80+90+100 = 400 & 0xFF = 144

    GPIOR0.value = result    # 12
    GPIOR1.value = lo_sum    # 150 = 0x96
    GPIOR2.value = hi_sum    # 400 & 0xFF = 144 = 0x90
    asm("BREAK")

    while True:
        pass
