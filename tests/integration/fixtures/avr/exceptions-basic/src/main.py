# exceptions-basic: try/except and raise using avr-libc setjmp/longjmp.
#
# A: raise inside try, caught by except -> prints "caught"
# B: no raise, except not triggered -> prints "ok"
#
# Expected UART output:
#   EXNS
#   A:caught
#   B:ok
from pymcu.types import uint8
from pymcu.hal.uart import UART
from pymcu.time import delay_ms
from pymcu.exceptions import ValueError


def risky(x: uint8) -> uint8:
    if x == 0:
        raise ValueError
    return 42


def main():
    uart = UART(9600)
    uart.println("EXNS")

    # A: raise is triggered, except catches it
    try:
        result: uint8 = risky(0)
        uart.println("A:missed")
    except ValueError:
        uart.println("A:caught")

    # B: no raise, normal return
    try:
        result2: uint8 = risky(1)
        uart.println("B:ok")
    except ValueError:
        uart.println("B:caught")

    while True:
        delay_ms(1000)
