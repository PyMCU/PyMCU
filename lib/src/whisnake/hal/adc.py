from whisnake.chips import __CHIP__
from whisnake.types import uint8, uint16, inline


class AnalogPin:

    @inline
    def __init__(self, channel: str):
        match __CHIP__.name:
            case "atmega328p":
                from whisnake.hal._adc.atmega328p import adc_channel_admux, adc_init
                # Resolve channel to ADMUX value once and store it.
                # All subsequent reads use self._admux directly -- no string dispatch.
                self._admux = adc_channel_admux(channel)
                adc_init(self._admux)
            case "pic16f877a":
                from whisnake.hal._adc.pic16f877a import adc_init
                self.channel = channel
                adc_init(channel)
            case "pic16f18877":
                from whisnake.hal._adc.pic16f18877 import adc_init
                self.channel = channel
                adc_init(channel)
            case "pic18f45k50":
                from whisnake.hal._adc.pic18f45k50 import adc_init
                self.channel = channel
                adc_init(channel)

    @inline
    def start(self):
        match __CHIP__.name:
            case "atmega328p":
                from whisnake.hal._adc.atmega328p import adc_start
                adc_start()
            case "pic16f877a":
                from whisnake.hal._adc.pic16f877a import adc_start
                adc_start(self.channel)
            case "pic16f18877":
                from whisnake.hal._adc.pic16f18877 import adc_start
                adc_start(self.channel)
            case "pic18f45k50":
                from whisnake.hal._adc.pic18f45k50 import adc_start
                adc_start(self.channel)

    # Trigger conversion and return the raw 10-bit result (0-1023).
    @inline
    def read(self) -> uint16:
        match __CHIP__.name:
            case "atmega328p":
                from whisnake.hal._adc.atmega328p import adc_read
                return adc_read()

    # Start a single conversion with ADC Interrupt Enable (ADIE=1).
    # ATmega328P vector: byte 0x002A / word 0x0015.
    # Define @interrupt(0x002A) and call read_result() from inside that ISR.
    @inline
    def start_conversion(self):
        match __CHIP__.name:
            case "atmega328p":
                from whisnake.hal._adc.atmega328p import adc_start_int
                adc_start_int()

    # Read the raw 10-bit result without triggering a new conversion.
    # Call from the ADC complete ISR or after polling the ADIF flag.
    @inline
    def read_result(self) -> uint16:
        match __CHIP__.name:
            case "atmega328p":
                from whisnake.hal._adc.atmega328p import adc_read_result
                return adc_read_result()

    # Trigger conversion and return result scaled to 16-bit (0-65535).
    @inline
    def read_u16(self) -> uint16:
        match __CHIP__.name:
            case "atmega328p":
                from whisnake.hal._adc.atmega328p import adc_read_u16
                return adc_read_u16()
