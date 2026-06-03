# exceptions-finally: try/except/finally and try/finally semantics.
#
# A: exception raised, caught, finally always runs -> "A:caught" then "A:fin"
# B: no exception, finally still runs -> "B:ok" then "B:fin"
# C: try/finally without except, exception propagates after finally -> "C:fin" (then halt)
#    (halts via __pymcu_unhandled_exn, so C only prints C:fin)
#
# Expected UART output:
#   FINALLY
#   A:caught
#   A:fin
#   B:ok
#   B:fin
#   DONE
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
    uart.println("FINALLY")

    # A: exception raised and caught, finally runs after handler
    try:
        result: uint8 = risky(0)
        uart.println("A:missed")
    except ValueError:
        uart.println("A:caught")
    finally:
        uart.println("A:fin")

    # B: no exception, finally still executes
    try:
        result2: uint8 = risky(1)
        uart.println("B:ok")
    except ValueError:
        uart.println("B:missed")
    finally:
        uart.println("B:fin")

    uart.println("DONE")

    while True:
        delay_ms(1000)
