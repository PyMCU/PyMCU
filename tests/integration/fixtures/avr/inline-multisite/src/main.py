# PyMCU -- inline-multisite: @inline function called at multiple call sites.
#
# Each @inline contains conditional branches (if-statements), which generate
# local labels. Calling the same @inline 3+ times in the same function scope
# must produce unique label prefixes per call site; otherwise the assembler
# would encounter duplicate labels and fail.
#
# To force real branch emission (prevent constant folding), arguments are
# loaded from hardware I/O registers (GPIOR0/GPIOR1/GPIOR2) immediately
# before each call. The compiler cannot propagate I/O reads as constants.
#
# Checkpoints:
#   1 -- min_u8 called 3 times: 10, 5, 7
#   2 -- clamp called 3 times (above-hi / below-lo / in-range): 100, 10, 50
#   3 -- abs_diff called 3 times (a>b / b>a / equal): 7, 7, 0
#
# Data-space addresses:
#   GPIOR0 = 0x3E   GPIOR1 = 0x4A   GPIOR2 = 0x4B
from pymcu.chips.atmega328p import GPIOR0, GPIOR1, GPIOR2
from pymcu.types import uint8, inline, asm


@inline
def min_u8(a: uint8, b: uint8) -> uint8:
    if a < b:
        return a
    return b


@inline
def clamp(x: uint8, lo: uint8, hi: uint8) -> uint8:
    if x < lo:
        return lo
    if x > hi:
        return hi
    return x


@inline
def abs_diff(a: uint8, b: uint8) -> uint8:
    if a >= b:
        return a - b
    return b - a


def main():
    # --- Checkpoint 1: min_u8 at 3 call sites with runtime args ---
    # Site 1: min(10, 20) = 10  (a < b path)
    GPIOR0.value = 10
    GPIOR1.value = 20
    a0: uint8 = GPIOR0.value
    b0: uint8 = GPIOR1.value
    r0: uint8 = min_u8(a0, b0)

    # Site 2: min(30, 5) = 5  (b <= a path)
    GPIOR0.value = 30
    GPIOR1.value = 5
    a1: uint8 = GPIOR0.value
    b1: uint8 = GPIOR1.value
    r1: uint8 = min_u8(a1, b1)

    # Site 3: min(7, 7) = 7  (equal: neither < the other, takes 'return b' path)
    GPIOR0.value = 7
    GPIOR1.value = 7
    a2: uint8 = GPIOR0.value
    b2: uint8 = GPIOR1.value
    r2: uint8 = min_u8(a2, b2)

    GPIOR0.value = r0   # 10
    GPIOR1.value = r1   # 5
    GPIOR2.value = r2   # 7
    asm("BREAK")

    # --- Checkpoint 2: clamp at 3 call sites (all three internal paths) ---
    # Site 1: clamp(200, 10, 100) = 100  (x > hi path)
    GPIOR0.value = 200
    GPIOR1.value = 10
    GPIOR2.value = 100
    x0: uint8 = GPIOR0.value
    lo0: uint8 = GPIOR1.value
    hi0: uint8 = GPIOR2.value
    c0: uint8 = clamp(x0, lo0, hi0)

    # Site 2: clamp(5, 10, 100) = 10  (x < lo path)
    GPIOR0.value = 5
    GPIOR1.value = 10
    GPIOR2.value = 100
    x1: uint8 = GPIOR0.value
    lo1: uint8 = GPIOR1.value
    hi1: uint8 = GPIOR2.value
    c1: uint8 = clamp(x1, lo1, hi1)

    # Site 3: clamp(50, 10, 100) = 50  (in range: no clamping)
    GPIOR0.value = 50
    GPIOR1.value = 10
    GPIOR2.value = 100
    x2: uint8 = GPIOR0.value
    lo2: uint8 = GPIOR1.value
    hi2: uint8 = GPIOR2.value
    c2: uint8 = clamp(x2, lo2, hi2)

    GPIOR0.value = c0   # 100
    GPIOR1.value = c1   # 10
    GPIOR2.value = c2   # 50
    asm("BREAK")

    # --- Checkpoint 3: abs_diff at 3 call sites ---
    # Site 1: abs_diff(10, 3) = 7  (a >= b path: returns a - b)
    GPIOR0.value = 10
    GPIOR1.value = 3
    p0: uint8 = GPIOR0.value
    q0: uint8 = GPIOR1.value
    d0: uint8 = abs_diff(p0, q0)

    # Site 2: abs_diff(3, 10) = 7  (b > a path: returns b - a)
    GPIOR0.value = 3
    GPIOR1.value = 10
    p1: uint8 = GPIOR0.value
    q1: uint8 = GPIOR1.value
    d1: uint8 = abs_diff(p1, q1)

    # Site 3: abs_diff(5, 5) = 0  (equal: a >= b, returns a - b = 0)
    GPIOR0.value = 5
    GPIOR1.value = 5
    p2: uint8 = GPIOR0.value
    q2: uint8 = GPIOR1.value
    d2: uint8 = abs_diff(p2, q2)

    GPIOR0.value = d0   # 7
    GPIOR1.value = d1   # 7
    GPIOR2.value = d2   # 0
    asm("BREAK")

    while True:
        pass
