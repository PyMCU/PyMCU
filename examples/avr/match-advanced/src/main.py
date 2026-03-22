# PyMCU -- match-advanced: PEP 634 extensions
#
# Demonstrates:
#   F2: match guard     case x if x > 50:
#   F3: sequence pattern  case [0xFF, cmd, data]:
#   F4: capture pattern   case v:  /  case 1|2 as n:
#
# Output on UART (9600 baud):
#   "MA\n"    -- boot banner
#   "G:HI\n"  -- guard: val=80 > 50 -> "HI"
#   "G:LO\n"  -- guard: val=20 not > 50 -> "LO"
#   "S:2A\n"  -- sequence: packet [0xFF,42,0] -> cmd=42=0x2A
#   "C:07\n"  -- capture: 7 matches wildcard -> bound to v=7
#   "O:02\n"  -- OR-capture: 2 matches 1|2 as n -> n=2
#
from whipsnake.types import uint8
from whipsnake.hal.uart import UART


def nibble_hi(val: uint8) -> uint8:
    n: uint8 = (val >> 4) & 0x0F
    if n < 10:
        return n + 48
    return n + 55


def nibble_lo(val: uint8) -> uint8:
    n: uint8 = val & 0x0F
    if n < 10:
        return n + 48
    return n + 55


def main():
    uart = UART(9600)
    uart.println("MA")

    # F2: match guard
    val: uint8 = 80
    match val:
        case x if x > 50:
            uart.println("G:HI")
        case _:
            uart.println("G:LO")

    val = 20
    match val:
        case x if x > 50:
            uart.println("G:HI")
        case _:
            uart.println("G:LO")

    # F3: sequence pattern [0xFF, cmd, data]
    packet: uint8[3] = [0xFF, 42, 0]
    match packet:
        case [0xFF, cmd, data]:
            uart.write('S')
            uart.write(':')
            uart.write(nibble_hi(cmd))
            uart.write(nibble_lo(cmd))
            uart.write('\n')
        case _:
            uart.println("S:XX")

    # F4: bare capture pattern binds value
    probe: uint8 = 7
    match probe:
        case v:
            uart.write('C')
            uart.write(':')
            uart.write(nibble_hi(v))
            uart.write(nibble_lo(v))
            uart.write('\n')

    # F4: OR pattern with as-capture
    code: uint8 = 2
    match code:
        case 1 | 2 as n:
            uart.write('O')
            uart.write(':')
            uart.write(nibble_hi(n))
            uart.write(nibble_lo(n))
            uart.write('\n')
        case _:
            uart.println("O:XX")

    while True:
        pass
