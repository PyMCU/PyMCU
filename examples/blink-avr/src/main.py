# ATmega328P: Classic LED blink with button input
# Demonstrates: Pin HAL, pin read, while loop, conditional, delay, uart
#
# Hardware: Arduino Uno or any ATmega328P board
#   - LED on PB5 (Arduino pin 13, built-in LED)
#   - Button on PD2 (active low, uses internal pull-up)
#   - UART on TX/RX (9600 baud)
#
from pymcu.hal.gpio import Pin
from pymcu.hal.uart import UART
from pymcu.time import delay_ms

def main():
    led = Pin("PB5", Pin.OUT)
    button = Pin("PD2", Pin.IN, pull=Pin.PULL_UP)
    uart = UART(9600)
    
    # Send boot message
    uart.println("Hello")

    while True:
        if button.value() == 0:
            led.high()
            uart.write(49) # '1'
        else:
            led.low()
            uart.write(48) # '0'
        
        delay_ms(100)
