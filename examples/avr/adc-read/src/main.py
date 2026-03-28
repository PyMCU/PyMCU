# ATmega328P: ADC single-channel continuous read
#
# Hardware: Arduino Uno or ATmega328P board
#   - Potentiometer (or any analog source) on PC0 (A0)
#   - Serial terminal at 9600 baud to observe raw 8-bit result
#
# adc.read() triggers a conversion, polls ADSC until clear, and returns
# the raw 10-bit result (0-1023). Right-shifting by 2 scales it to 8 bits
# (0-255) for single-byte UART output.
#
from pymcu.types import uint8, uint16
from pymcu.hal.adc import AnalogPin
from pymcu.hal.uart import UART
from pymcu.time import delay_ms


def main():
    uart = UART(9600)
    adc  = AnalogPin("PC0")

    uart.println("ADC")

    while True:
        raw:    uint16 = adc.read()
        result: uint8  = raw >> 2   # 10-bit -> 8-bit
        uart.write(result)
        delay_ms(100)
