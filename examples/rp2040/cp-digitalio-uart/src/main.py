# CircuitPython-style blink + UART echo on the Raspberry Pi Pico.
#
# This exact file runs unmodified under CircuitPython on a Pico.
# PyMCU compiles it to bare-metal Thumb firmware with zero runtime overhead.
#
# Try it under CircuitPython by saving as code.py on the Pico.
# Compile with PyMCU:
#   pymcu build   (produces dist/firmware.bin)
import board
import digitalio
import busio
from pymcu.types import uint8


def main():
    led = digitalio.DigitalInOut(board.LED)
    led.direction = digitalio.Direction.OUTPUT

    uart = busio.UART(board.TX, board.RX, baudrate=115200)
    uart.write(b"READY\r\n")

    buf: uint8[1] = bytearray(1)
    while True:
        uart.readinto(buf)
        # LED mirrors the LSB of the received byte.
        if buf[0] & 1:
            led.value = 1
        else:
            led.value = 0
        uart.write(buf)
