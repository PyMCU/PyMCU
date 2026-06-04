# AVR: T-flag error propagation demo
#
# Demonstrates the new ABI interna de PyMCU:
#   - raise         -> LDI R22, code; SET; RET   (3 instrucciones, sin longjmp)
#   - return (ok)   -> CLT; RET                  (CanFail success path)
#
# safe_div: raises ValueError if b == 0
# safe_sub: raises ValueError if a < b (underflow protection)
#
# Expected flash reduction vs SJLJ: ~10 instrucciones → 3 instrucciones por raise.
#
from pymcu.types import uint8
from pymcu.hal.uart import UART
from pymcu.exceptions import ValueError


def safe_div(a: uint8, b: uint8) -> uint8:
    if b == 0:
        raise ValueError
    return a // b


def safe_sub(a: uint8, b: uint8) -> uint8:
    if a < b:
        raise ValueError
    return a - b


def main():
    uart = UART("USART0", 9600)
    r1 = safe_div(20, 4)
    uart.write(r1)
    r2 = safe_sub(10, 3)
    uart.write(r2)
