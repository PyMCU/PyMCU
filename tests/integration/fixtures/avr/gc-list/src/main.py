# PyMCU -- gc-list: list[T] heap-allocated list operations
#
# Verifies:
#   - list[T] = list() allocates a GC-managed list
#   - append() adds elements (fast path, no realloc)
#   - len() returns the correct runtime length
#   - x[i] indexing reads the correct element
#   - for v in x: iterates all elements in order
#
# Output on UART (9600 baud):
#   "LIST\n"  -- boot banner
#   "L:Y\n"   -- len(x) == 3
#   "I:Y\n"   -- x[1] == 20
#   "S:Y\n"   -- sum via for-in == 60
#   "DONE\n"  -- all checks passed
#
from pymcu.hal.uart import UART

def main():
    uart = UART(9600)
    uart.println("LIST")

    x: list[uint8] = list()
    x.append(10)
    x.append(20)
    x.append(30)

    n: uint8 = len(x)
    if n == 3:
        uart.println("L:Y")
    else:
        uart.println("L:N")

    v1: uint8 = x[1]
    if v1 == 20:
        uart.println("I:Y")
    else:
        uart.println("I:N")

    total: uint8 = 0
    for v in x:
        total = total + v

    if total == 60:
        uart.println("S:Y")
    else:
        uart.println("S:N")

    uart.println("DONE")

    while True:
        pass
