# MicroPython-style UART echo on the Raspberry Pi Pico.
#
# This exact file runs unmodified under MicroPython on a Pico.
# PyMCU compiles it to bare-metal Thumb firmware with zero runtime overhead.
#
# Try it under MicroPython:
#   >>> import main; main.main()
#
# Compile with PyMCU:
#   pymcu build   (produces dist/firmware.bin)
from machine import Pin, UART


def main():
    uart = UART(0, 115200)
    led = Pin(25, Pin.OUT)
    uart.println("READY")
    while True:
        c: int = uart.read()
        led.toggle()
        uart.write(c)
