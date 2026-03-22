# ATmega328P: ADC interrupt-driven sampling on PC0 (ADC0)
#
# Uses AnalogPin.start_conversion() which sets ADIE=1 before ADSC=1.
# ADC complete ISR fires at vector byte 0x002A / word 0x0015 on ATmega328P.
# ISR reads ADCL/ADCH directly (no stack locals) and stores the low byte
# in GPIOR1, then signals the main loop via GPIOR0[1].
#
# ADCSRA = 0x87: ADEN=1, ADPS[2:0]=111 (prescaler 128, ~125 kHz ADC clock)
# ADMUX  = 0x40: REFS1:0=01 (AVCC), MUX3:0=0000 (ADC0/PC0)
#
# Hardware: Arduino Uno
#   - ADC0 = PC0 (analog in)
#   - UART TX 9600 baud
#
from whipsnake.types import uint8, uint16, interrupt, asm
from whipsnake.chips.atmega328p import GPIOR0, GPIOR1, ADCL, ADCH, ADCSRA
from whipsnake.hal.uart import UART
from whipsnake.hal.adc import AnalogPin


@interrupt(0x002A)
def adc_isr():
    # Read result directly from ADCL/ADCH with no inline stack locals.
    # Must read ADCL first to lock ADCH.
    GPIOR1.value = ADCL.value
    GPIOR0[1] = 1


def main():
    uart = UART(9600)
    adc  = AnalogPin("PC0")

    GPIOR0[1] = 0
    GPIOR1.value = 0
    asm("SEI")

    uart.println("ADC IRQ")

    # Kick off first conversion with interrupt enabled
    adc.start_conversion()

    while True:
        if GPIOR0[1] == 1:
            GPIOR0[1] = 0
            result: uint8 = GPIOR1.value
            uart.write(result)
            uart.write('\n')
            # Start next conversion
            adc.start_conversion()
