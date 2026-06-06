# Blink the Raspberry Pi Pico on-board LED (GP25).
from pymcu.hal.gpio import Pin
from pymcu.time import delay_ms


def main():
    led = Pin(25, Pin.OUT)
    while True:
        led.high()
        delay_ms(500)
        led.low()
        delay_ms(500)
