# no-entrypoint: script without def main() -- top-level code style
# Demonstrates: synthesized main from top-level executable statements
# Tests: global mutable variable, while loop, uart output, delay
#
# This script has NO def main(): wrapper.  The compiler synthesizes one
# automatically from the top-level executable statements.
from pymcu.hal.uart import UART
from pymcu.hal.gpio import Pin
from pymcu.time import delay_ms
from pymcu.types import uint8, uint16

uart = UART(9600)
led = Pin("PB5", Pin.OUT)
count: uint8 = 0

uart.println("NOMAIN")

while True:
    led.high()
    delay_ms(500)
    led.low()
    delay_ms(500)
    count = count + 1
    if count == 3:
        uart.println("THREE")
        count = 0
