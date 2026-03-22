from whipsnake.types import uint8, inline
from whipsnake.chips.pic16f18877 import ADCON0, ADCON1, ADPCH, ADCLK, ADREF, ADRESL, ADRESH


@inline
def adc_init(channel: str):
    ADCLK = 0x01
    ADREF = 0x00
    ADCON0 = 0x84
    if channel == "RA0":
        ADPCH = 0x00
    elif channel == "RA1":
        ADPCH = 0x01
    elif channel == "RA2":
        ADPCH = 0x02
    elif channel == "RA3":
        ADPCH = 0x03
    elif channel == "RA4":
        ADPCH = 0x04
    elif channel == "RA5":
        ADPCH = 0x05


@inline
def adc_start(channel: str):
    ADCON0[0] = 1
