from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint16, inline

class AnalogPin:
    @inline
    def __init__(self: uint8, channel: str):
        self.channel = channel
        if __CHIP__.name == "pic16f877a":
            from pymcu.hal._adc.pic16f877a import adc_init
            adc_init(channel)
        elif __CHIP__.name == "pic16f18877":
            from pymcu.hal._adc.pic16f18877 import adc_init
            adc_init(channel)
        elif __CHIP__.name == "pic18f45k50":
            from pymcu.hal._adc.pic18f45k50 import adc_init
            adc_init(channel)
        elif __CHIP__.name == "atmega328p":
            from pymcu.hal._adc.atmega328p import adc_init
            adc_init(channel)

    @inline
    def start(self: uint8):
        if __CHIP__.name == "pic16f877a":
            from pymcu.hal._adc.pic16f877a import adc_start
            adc_start(self.channel)
        elif __CHIP__.name == "pic16f18877":
            from pymcu.hal._adc.pic16f18877 import adc_start
            adc_start(self.channel)
        elif __CHIP__.name == "pic18f45k50":
            from pymcu.hal._adc.pic18f45k50 import adc_start
            adc_start(self.channel)
        elif __CHIP__.name == "atmega328p":
            from pymcu.hal._adc.atmega328p import adc_start
            adc_start(self.channel)

    # Triggers ADC conversion and returns the raw 10-bit result (0-1023).
    @inline
    def read(self: uint8) -> uint16:
        if __CHIP__.name == "atmega328p":
            from pymcu.hal._adc.atmega328p import adc_read
            return adc_read()

    # Start a single conversion with the ADC Interrupt Enable bit set (ADIE=1).
    # The ADC complete interrupt fires at vector byte 0x002A / word 0x0015 on ATmega328P.
    # Use @interrupt(0x002A) and call read_result() from inside that ISR.
    @inline
    def start_conversion(self: uint8):
        if __CHIP__.name == "atmega328p":
            from pymcu.hal._adc.atmega328p import adc_start_int
            adc_start_int()

    # Read the raw 10-bit ADC result (0-1023) without triggering a new conversion.
    # Call this from the ADC complete ISR or after polling the ADIF flag.
    @inline
    def read_result(self: uint8) -> uint16:
        if __CHIP__.name == "atmega328p":
            from pymcu.hal._adc.atmega328p import adc_read_result
            return adc_read_result()

    # Triggers ADC conversion and returns the result scaled to 16-bit (0-65535).
    @inline
    def read_u16(self: uint8) -> uint16:
        if __CHIP__.name == "atmega328p":
            from pymcu.hal._adc.atmega328p import adc_read_u16
            return adc_read_u16()
