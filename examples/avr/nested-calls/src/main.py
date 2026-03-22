# ATmega328P: Nested non-inline function calls
#
# Demonstrates a 3-level function call chain with argument passing and
# return values. Each level calls the next level, collecting results.
#
# Call chain:
#   main() -> encode_byte(b) -> nibble_to_hex(n) -> (returns char)
#
# encode_byte(b: uint8) -> uint8:
#   Sends high nibble + low nibble as hex ASCII over UART.
#   Returns the XOR of both nibble chars (a checksum byte).
#
# nibble_to_hex(n: uint8) -> uint8:
#   Converts 0-15 -> ASCII '0'-'9'/'A'-'F'.
#
# Tests:
#   - 3-level non-inline call chain
#   - Functions with 1 argument and uint8 return value
#   - Calling convention: arg -> R24, return -> R24
#   - Two calls to same function within one function body
#
# Hardware: Arduino Uno
#   UART TX on PD1 at 9600 baud
#
# Output format:
#   Boot:  "HEX ENCODE\n"
#   Then:  "00\n01\n02\n...\nFF\n" cycling, each byte on its own line
#
from whipsnake.types import uint8
from whipsnake.hal.uart import UART


# Convert a 4-bit nibble (0-15) to its ASCII hex character.
def nibble_to_hex(n: uint8) -> uint8:
    if n < 10:
        return n + 48        # '0'-'9' = 48-57
    else:
        return n + 55        # 'A'-'F' = 65-70


# Encode one byte as two hex chars sent via UART.
# Returns XOR of both chars (as a sanity check value).
def encode_byte(uart_data: uint8, b: uint8) -> uint8:
    hi: uint8 = nibble_to_hex((b >> 4) & 0x0F)
    lo: uint8 = nibble_to_hex(b & 0x0F)
    chk: uint8 = hi ^ lo
    return chk


def main():
    uart = UART(9600)
    uart.println("HEX ENCODE")

    val: uint8 = 0
    while True:
        hi: uint8 = nibble_to_hex((val >> 4) & 0x0F)
        lo: uint8 = nibble_to_hex(val & 0x0F)
        chk: uint8 = encode_byte(val, val)   # also tests 2-arg call
        uart.write(hi)
        uart.write(lo)
        uart.write(chk)    # send checksum byte (hi ^ lo)
        uart.write('\n')
        val += 1
