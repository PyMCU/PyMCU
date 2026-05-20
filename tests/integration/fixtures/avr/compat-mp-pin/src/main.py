# MicroPython machine.Pin integration test fixture
#
# Verifies that machine.Pin(13, Pin.OUT) correctly maps Arduino Uno pin 13
# to PB5 at compile time via _arduino_pin_name DCE.
#
# Expected behaviour:
#   LED (D13 = PB5) blinks at 1 Hz: 1000 ms HIGH, 1000 ms LOW, repeat.
#
from machine import Pin
from pymcu.time import delay_ms


def main():
    led = Pin(13, Pin.OUT)   # 13 -> "PB5" resolved at compile time
    while True:
        led.on()
        delay_ms(1000)
        led.off()
        delay_ms(1000)
