# implicit-int: use Python's built-in `int` type (maps to int16) without
# importing pymcu.types.  Tests both annotation syntax and cast syntax.
#
# Expected UART output:
#   A:PASS   (int annotation works, signed arithmetic correct)
#   B:PASS   (int() cast works without import)
#   C:PASS   (negative int value, i.e. signed int16)
from pymcu.hal.uart import UART
from pymcu.time import delay_ms

def main():
    uart = UART(9600)
    uart.println("IINT")

    # A: int annotation (no pymcu.types import required)
    x: int = 100
    y: int = 200
    z: int = x + y
    if z == 300:
        uart.println("A:PASS")
    else:
        uart.println("A:FAIL")

    # B: int() cast (no pymcu.types import required)
    raw: int = 42
    casted: int = int(raw)
    if casted == 42:
        uart.println("B:PASS")
    else:
        uart.println("B:FAIL")

    # C: signed int16 can hold negative values
    neg: int = 0
    neg = neg - 1
    if neg == -1:
        uart.println("C:PASS")
    else:
        uart.println("C:FAIL")

    while True:
        delay_ms(1000)
