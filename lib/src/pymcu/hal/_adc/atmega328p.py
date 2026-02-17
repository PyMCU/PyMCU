from pymcu.types import uint8, inline
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
