# t-flag-errors: Integration test fixture for the T-flag error propagation model.
#
# Verifies the SET/CLT/BRTS ABI that replaced setjmp/longjmp:
#
#   CanFail function error path : LDD R22, code; SET; RET
#   CanFail function happy path : [result in R24];  CLT; RET
#   try/except call site        : BRTC skip; RJMP catch_dispatch (zero cost when T=0)
#
# Scenarios (all single exception-type to stay independent of exception-code
# constant folding, which is tested separately):
#
#   A - raise ValueError caught by except ValueError               -> "A:caught"
#   B - no raise, except not triggered, happy path completes       -> "B:ok"
#   C - CanFail with two args: raise on bad input                  -> "C:caught"
#   D - T flag is CLT'd after success; subsequent try not fooled   -> "D:ok"
#   E - return value correct from CanFail success                  -> "E:0A" (10)
#   F - three sequential raises all caught, counter reaches 3      -> "F:03"
#
# Expected UART output (in order):
#   TFLAG
#   A:caught
#   B:ok
#   C:caught
#   D:ok
#   E:0A
#   F:03
#   DONE
#
from pymcu.types import uint8
from pymcu.hal.uart import UART
from pymcu.time import delay_ms
from pymcu.exceptions import ValueError


def must_positive(x: uint8) -> uint8:
    if x == 0:
        raise ValueError
    return x


def safe_add(a: uint8, b: uint8) -> uint8:
    if a > 200:
        raise ValueError
    return a + b


def main():
    uart = UART(9600)
    uart.println("TFLAG")

    # A: raise ValueError caught by except ValueError
    try:
        v: uint8 = must_positive(0)
        uart.println("A:missed")
    except ValueError:
        uart.println("A:caught")

    # B: no raise, happy path, except not triggered
    try:
        v2: uint8 = must_positive(7)
        uart.println("B:ok")
    except ValueError:
        uart.println("B:missed")

    # C: CanFail with two args; raise on a > 200
    try:
        v3: uint8 = safe_add(201, 1)
        uart.println("C:missed")
    except ValueError:
        uart.println("C:caught")

    # D: T flag must be CLT'd after successful CanFail call.
    #    must_positive(1) succeeds -> CLT -> T=0 -> next try sees T=0 -> no spurious catch.
    must_positive(1)
    try:
        v4: uint8 = must_positive(1)
        uart.println("D:ok")
    except ValueError:
        uart.println("D:spurious")

    # E: return value from CanFail success is correct (safe_add(8,2) = 10 = 0x0A)
    try:
        res: uint8 = safe_add(8, 2)
        uart.write('E')
        uart.write(':')
        uart.write_hex(res)
        uart.write('\n')
    except ValueError:
        uart.println("E:missed")

    # F: three sequential raises; each caught independently; counter = 3 = 0x03
    counter: uint8 = 0

    try:
        must_positive(0)
    except ValueError:
        counter = counter + 1

    try:
        must_positive(0)
    except ValueError:
        counter = counter + 1

    try:
        must_positive(0)
    except ValueError:
        counter = counter + 1

    uart.write('F')
    uart.write(':')
    uart.write_hex(counter)
    uart.write('\n')

    uart.println("DONE")

    while True:
        delay_ms(1000)
