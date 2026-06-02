# random-led -- random blink pattern with ADC noise seed
#
# Reads ADC channel 0 (floating pin) for hardware noise to seed the PRNG,
# then blinks the built-in LED (PB5 / Arduino D13) with random on/off intervals.
# Also prints the random delay values over UART at 9600 baud.
#
# Demonstrates: pymcu.random, pymcu.math.map_range, pymcu.math.constrain
from pymcu.types import uint8, uint16
from pymcu.hal.gpio import Pin
from pymcu.hal.uart import UART
from pymcu.hal.adc import AnalogPin
from pymcu.time import delay_ms
from pymcu.random import randomSeed, random
from pymcu.math import map_range, constrain


def main():
    led = Pin("PB5", Pin.OUT)
    uart = UART(9600)
    adc = AnalogPin(0)

    noise: uint16 = adc.read()
    randomSeed(noise)

    uart.write_str("random-led start\n")

    while True:
        on_ms: uint16 = map_range(random(256), 0, 255, 50, 500)
        off_ms: uint16 = constrain(random(300), 50, 300)

        led.high()
        delay_ms(on_ms)
        led.low()
        delay_ms(off_ms)
