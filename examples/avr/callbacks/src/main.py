# ATmega328P: Simulated callback dispatch via uint8 function selectors
#
# Stress-tests:
#   - Non-inline user functions with parameters and return values (RCALL/RET)
#   - Nested non-inline call chains: main → apply → double_value / invert_value / shift_left_one
#   - GCC-AVR calling convention: arg0 → R24, arg1 → R22 (implemented in AVRCodeGen)
#   - match/case dispatch on uint8 class-attribute constants inside a non-inline function
#   - Inner while loop with uint8 counter, CALL result forwarded to UART write
#
# Hardware: Arduino Uno
#   UART TX on PD1 (Arduino pin 1) — connect to PC at 9600 baud
#   Open a hex viewer (e.g. screen /dev/ttyUSB0 9600) to see raw bytes.
#
# Protocol per outer loop (one "pass"):
#   Byte 0   : active callback ID (0=DOUBLE, 1=INVERT, 2=SHIFT_L)
#   Bytes 1-8: apply(cb, 0) .. apply(cb, 7)  — 8 processed values
#   Byte 9   : 0x0A (newline separator)
#
# Expected output for first three passes (hex):
#   DOUBLE:  00  00 00 02 04 06 08 0A 0C 0E  0A
#   INVERT:  01  FF FE FD FC FB FA F9 F8  0A
#   SHIFT_L: 02  00 02 04 06 08 0A 0C 0E  0A  (same as DOUBLE for uint8)

from whisnake.types import uint8
from whisnake.hal.uart import UART


# Callback selector constants — use dotted names so match/case treats them
# as value patterns (bare names are capture patterns in PyMCU)
class CB:
    DOUBLE  = 0
    INVERT  = 1
    SHIFT_L = 2


# --- Non-inline helper functions (compiled to RCALL/RET) ---

def double_value(x: uint8) -> uint8:
    result: uint8 = x + x
    return result


def invert_value(x: uint8) -> uint8:
    result: uint8 = x ^ 0xFF
    return result


def shift_left_one(x: uint8) -> uint8:
    # Left shift by 1: equivalent to x + x for uint8
    result: uint8 = x + x
    return result


def apply(cb_id: uint8, x: uint8) -> uint8:
    # Dispatch to the selected callback.
    # Tests: match/case in a non-inline function body; early return from case arms.
    match cb_id:
        case CB.DOUBLE:
            return double_value(x)
        case CB.INVERT:
            return invert_value(x)
        case CB.SHIFT_L:
            return shift_left_one(x)
        case _:
            return x


def main():
    uart = UART(9600)
    uart.println("CALLBACKS")

    cb: uint8 = CB.DOUBLE

    while True:
        uart.write(cb)          # header: current callback ID

        i: uint8 = 0
        while i < 8:
            r: uint8 = apply(cb, i)
            uart.write(r)
            i += 1

        uart.write('\n')          # newline (0x0A) frame separator

        # Advance to next callback (cycle: DOUBLE → INVERT → SHIFT_L → DOUBLE)
        if cb == CB.DOUBLE:
            cb = CB.INVERT
        elif cb == CB.INVERT:
            cb = CB.SHIFT_L
        else:
            cb = CB.DOUBLE
