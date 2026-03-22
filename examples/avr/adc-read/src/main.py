# ATmega328P: ADC single-channel continuous read
# Tests: AnalogPin HAL, direct register polling (ADCSRA), ADCL read, uint8 variable
#
# Hardware: Arduino Uno or ATmega328P board
#   - Potentiometer (or any analog source) on PC0 (A0)
#   - Serial terminal at 9600 baud to observe raw 8-bit result
#
# ADC result is 10-bit; we read only ADCL (low 8 bits) for a simple 8-bit value.
#
from whisnake.types import uint8
from whisnake.hal.adc import AnalogPin
from whisnake.hal.uart import UART
from whisnake.chips.atmega328p import ADCSRA, ADCL
from whisnake.time import delay_ms


def main():
    uart = UART(9600)
    adc  = AnalogPin("PC0")

    uart.println("ADC")

    while True:
        # Trigger conversion (ADSC = ADCSRA bit 6)
        adc.start()

        # Wait for conversion complete: ADSC clears to 0 when done
        while ADCSRA[6] == 1:
            pass

        # Read low byte of 10-bit result (coarse 8-bit resolution)
        result: uint8 = ADCL[0]
        uart.write(result)

        delay_ms(100)
