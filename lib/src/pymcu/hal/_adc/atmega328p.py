from pymcu.types import uint8, uint16, inline
from pymcu.chips.atmega328p import ADMUX, ADCSRA, ADCSRB, ADCL, ADCH


@inline
def adc_init(channel: str):
    ADCSRA = 0x87
    if channel == "PC0":
        ADMUX = 0x40
    elif channel == "PC1":
        ADMUX = 0x41
    elif channel == "PC2":
        ADMUX = 0x42
    elif channel == "PC3":
        ADMUX = 0x43
    elif channel == "PC4":
        ADMUX = 0x44
    elif channel == "PC5":
        ADMUX = 0x45


@inline
def adc_start(channel: str):
    ADCSRA[6] = 1


# Start conversion, wait for ADSC (bit 6) to clear, then read ADCL/ADCH.
# Returns raw 10-bit result (0-1023).
@inline
def adc_read() -> uint16:
    ADCSRA[6] = 1
    while ADCSRA[6] == 1:
        pass
    lo: uint8 = ADCL.value
    hi: uint8 = ADCH.value
    result: uint16 = lo + hi * 256
    return result


# Start conversion, wait for ADSC (bit 6) to clear, then read ADCL/ADCH.
# Returns 10-bit result scaled to 16-bit range (0-65535) by multiplying by 64.
@inline
def adc_read_u16() -> uint16:
    ADCSRA[6] = 1
    while ADCSRA[6] == 1:
        pass
    lo: uint8 = ADCL.value
    hi: uint8 = ADCH.value
    result: uint16 = (lo + hi * 256) * 64
    return result
