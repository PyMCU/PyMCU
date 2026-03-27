# ATmega328P: UART echo — receive a byte and send it back
# Tests: UART.read(), UART.write(), while loop, local variable type annotation
#
# Hardware: Any ATmega328P board
#   - Connect a serial terminal at 9600 8N1
#   - Everything you type is echoed back
#
from pymcu.types import uint8
from pymcu.hal.uart import UART


def main():
    uart = UART(9600)

    uart.println("ECHO")

    while True:
        b: uint8 = uart.read()
        uart.write(b)
