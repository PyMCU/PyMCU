# PyMCU -- adc-temp: Internal temperature sensor (ADC channel 8) test fixture
#
# Verifies that AnalogPin("TEMP") selects ADMUX=0xC8 (internal 1.1V ref, ch8).
#
# Checkpoint written to GPIOR0 (0x3E):
#   GPIOR0 = ADMUX value after AnalogPin("TEMP") init -> must be 0xC8
#
from pymcu.types import uint8, asm
from pymcu.hal.adc import AnalogPin
from pymcu.chips.atmega328p import GPIOR0, ADMUX


def main():
    sensor = AnalogPin("TEMP")
    GPIOR0.value = ADMUX.value   # checkpoint: ADMUX after AnalogPin init
    asm("BREAK")
    while True:
        pass
