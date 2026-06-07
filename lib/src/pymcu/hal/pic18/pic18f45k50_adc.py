from pymcu.types import uint8, inline
from pymcu.chips.pic18f45k50 import ADCON0, ADCON1, ADCON2, ADRESH, ADRESL


@inline
def adc_init(channel: str):
    ADCON1 = 0x00
    ADCON2 = 0xA9
    if channel == "RA0":
        ADCON0 = 0x01
    elif channel == "RA1":
        ADCON0 = 0x05
    elif channel == "RA2":
        ADCON0 = 0x09
    elif channel == "RA3":
        ADCON0 = 0x0D
    elif channel == "RA5":
        ADCON0 = 0x11


@inline
def adc_start(channel: str):
    ADCON0[1] = 1
