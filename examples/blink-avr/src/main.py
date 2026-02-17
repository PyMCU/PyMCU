# ATmega328P: Classic LED blink with button input
# Demonstrates: Pin HAL, pin read, while loop, conditional
#
# Hardware: Arduino Uno or any ATmega328P board
#   - LED on PB5 (Arduino pin 13, built-in LED)
#   - Button on PD2 (active low, uses internal pull-up)
#
# The LED stays ON while the button is pressed, OFF otherwise.
# This avoids delay_ms (not yet implemented on AVR backend)
# and instead demonstrates real-time input polling.
#
from pymcu.hal.gpio import Pin

def main():
    led = Pin("PB5", Pin.OUT)
    button = Pin("PD2", Pin.IN, pull=Pin.PULL_UP)

    while True:
        if button.value() == 0:
            led.high()
        else:
            led.low()
