# ATmega328P: UART string/char demo
# Tests: UART.write_str(), UART.println(), single-char literals
#
# Hardware: Any ATmega328P board
#   - Connect a serial terminal at 9600 8N1
#
from whipsnake.types import uint8
from whipsnake.hal.uart import UART


def main():
    uart = UART(9600)

    # Test write_str (compile-time string expansion)
    uart.write_str("Hello, Whipsnake!\n")

    # Test println (write_str + newline)
    uart.println("UART string support works!")

    # Test single-char literals — lexer converts 'T' to ASCII 84, etc.
    uart.write('T')
    uart.write('e')
    uart.write('s')
    uart.write('t')
    uart.write('\n')

    while True:
        b: uint8 = uart.read()
        uart.write(b)
