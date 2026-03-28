# ATmega328P: ADC interrupt-driven sampling on PC0 (ADC0)
#
# adc.irq(adc_isr) registers the handler at the ADC Complete vector
# (byte 0x002A / word 0x0015), enables ADIE, and sets SEI -- no
# @interrupt decorator or asm("SEI") needed.
#
# The ISR reads ADCL first (latches ADCH), stores the low byte in GPIOR1,
# and signals the main loop via GPIOR0[1].
# Main loop prints the result over UART and triggers the next conversion.
#
# Hardware: Arduino Uno
#   - ADC0 = PC0 (analog input)
#   - UART TX 9600 baud
#
from pymcu.types import uint8
from pymcu.chips.atmega328p import GPIOR0, GPIOR1, ADCL, ADCH
from pymcu.hal.uart import UART
from pymcu.hal.adc import AnalogPin


def adc_isr():
    # Must read ADCL first to latch ADCH.
    GPIOR1.value = ADCL.value
    GPIOR0[1] = 1


def main():
    uart = UART(9600)
    adc  = AnalogPin("PC0")
    adc.irq(adc_isr)

    GPIOR0[1] = 0
    GPIOR1.value = 0
    uart.println("ADC IRQ")

    # Kick off first conversion
    adc.start_conversion()

    while True:
        if GPIOR0[1] == 1:
            GPIOR0[1] = 0
            result: uint8 = GPIOR1.value
            uart.write(result)
            uart.write('\n')
            # Start next conversion
            adc.start_conversion()
