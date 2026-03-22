# ATmega328P: Classic LED blink
# Demonstrates: Pin HAL, while loop, delay
#
# Hardware: Arduino Uno or any ATmega328P board
#   - Built-in LED on PB5 (Arduino digital pin 13)
#
from whisnake.hal.gpio import Pin
from whisnake.time import delay_ms

def main():
    led = Pin("PB5", Pin.OUT)

    while True:
        led.high()
        delay_ms(1000)
        led.low()
        delay_ms(1000)
