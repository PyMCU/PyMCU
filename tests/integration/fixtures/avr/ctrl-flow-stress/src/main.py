# PyMCU -- ctrl-flow-stress: control flow edge cases via BREAK checkpoints.
#
# Tests: continue in while, nested while, 4-arg function (R24/R22/R20/R18),
#        uint8 overflow via 4-arg call, all 3 early-return paths.
#
# Checkpoints:
#   1 -- continue: sum of odd numbers 1..9 = 25
#   2 -- nested while: 3 outer x 4 inner = 12 total increments
#   3 -- sum4(10, 20, 30, 40) = 100 (4-arg function, all argument registers)
#   4 -- sum4(200, 100, 0, 0) = 44 (300 mod 256, 4-arg with overflow)
#   5 -- classify(5/50/200) -> 1/2/3 (all three early-return paths)
#
# Data-space addresses:
#   GPIOR0 = 0x3E   GPIOR1 = 0x4A   GPIOR2 = 0x4B
from pymcu.chips.atmega328p import GPIOR0, GPIOR1, GPIOR2
from pymcu.types import uint8, asm


def sum4(a: uint8, b: uint8, c: uint8, d: uint8) -> uint8:
    """Exercises all four argument registers: a->R24, b->R22, c->R20, d->R18."""
    return a + b + c + d


def classify(x: uint8) -> uint8:
    """Returns 1 (below 10), 2 (10..100), or 3 (above 100) via early return."""
    if x < 10:
        return 1
    if x > 100:
        return 3
    return 2


def main():
    # --- Checkpoint 1: continue skips even j; sum of odd 1+3+5+7+9 = 25 ---
    s: uint8 = 0
    j: uint8 = 0
    while j < 10:
        j += 1
        if (j & 1) == 0:
            continue       # skip even numbers
        s += j
    GPIOR0.value = s       # expected: 25
    asm("BREAK")

    # --- Checkpoint 2: nested while 3 outer x 4 inner = 12 iterations ---
    outer: uint8 = 0
    count: uint8 = 0
    while outer < 3:
        inner: uint8 = 0
        while inner < 4:
            count += 1
            inner += 1
        outer += 1
    GPIOR0.value = count   # expected: 12
    asm("BREAK")

    # --- Checkpoint 3: 4-arg function sum4(10, 20, 30, 40) = 100 ---
    r3: uint8 = sum4(10, 20, 30, 40)
    GPIOR0.value = r3      # expected: 100
    asm("BREAK")

    # --- Checkpoint 4: 4-arg with overflow sum4(200, 100, 0, 0) = 300 mod 256 = 44 ---
    r4: uint8 = sum4(200, 100, 0, 0)
    GPIOR0.value = r4      # expected: 44
    asm("BREAK")

    # --- Checkpoint 5: all three early-return paths of classify ---
    GPIOR0.value = classify(5)    # x < 10    -> 1
    GPIOR1.value = classify(50)   # 10 <= x <= 100 -> 2
    GPIOR2.value = classify(200)  # x > 100   -> 3
    asm("BREAK")

    while True:
        pass
