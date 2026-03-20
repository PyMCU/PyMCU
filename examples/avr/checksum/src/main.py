# ATmega328P: XOR Checksum Accumulator
#
# Receives bytes over UART. After every 4 bytes, outputs the XOR checksum
# (one byte) followed by a newline.
#
# Tests:
#   - Accumulator pattern (XOR over received bytes)
#   - Byte counter with conditional reset
#   - uart.read() + uart.write() round-trip
#   - AugAssign XOR: acc ^= byte
#
# Hardware: Arduino Uno
#   UART TX/RX on PD1/PD0 at 9600 baud
#
# Protocol:
#   Send 4 bytes -> receive XOR(byte0, byte1, byte2, byte3), then '\n'
#
from pymcu.types import uint8
from pymcu.hal.uart import UART


def main():
    uart = UART(9600)
    uart.println("CHECKSUM")

    acc:   uint8 = 0   # XOR accumulator
    count: uint8 = 0   # bytes received so far (0-3)

    while True:
        byte: uint8 = uart.read()
        acc ^= byte
        count += 1
        if count == 4:
            uart.write(acc)
            uart.write('\n')
            acc = 0
            count = 0
