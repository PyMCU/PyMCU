# type-inference: variables without type annotations should infer from RHS.
#
# A: unannotated assignment from a function returning uint16 → value not truncated
# B: unannotated integer literal 300 (> 255) → inferred as int32, value preserved
# C: unannotated assignment from a uint16 arithmetic expression → correct result
#
# Expected UART output:
#   INFER
#   A:PASS
#   B:PASS
#   C:PASS
from pymcu.types import uint16
from pymcu.hal.uart import UART
from pymcu.time import delay_ms


def get_count() -> uint16:
    return 1000


def main():
    uart = UART(9600)
    uart.println("INFER")

    # A: unannotated from uint16-returning function (should not truncate to uint8)
    x = get_count()
    if x == 1000:
        uart.println("A:PASS")
    else:
        uart.println("A:FAIL")

    # B: unannotated integer literal > 255 (should not truncate to uint8)
    y = 300
    if y == 300:
        uart.println("B:PASS")
    else:
        uart.println("B:FAIL")

    # C: unannotated from uint16 arithmetic
    a: uint16 = 500
    b: uint16 = 600
    c = a + b
    if c == 1100:
        uart.println("C:PASS")
    else:
        uart.println("C:FAIL")

    while True:
        delay_ms(1000)
