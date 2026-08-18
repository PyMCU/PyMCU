from pymcu.types import uint8, uint16, inline
from pymcu.chips.pic16f18877 import ADCON0, ADCON1, ADPCH, ADCLK, ADREF, ADRESL, ADRESH


@inline
def adc_init(channel: str):
    ADCLK.value = 0x01
    ADREF.value = 0x00
    ADCON0.value = 0x84
    if channel == "RA0":
        ADPCH.value = 0x00
    elif channel == "RA1":
        ADPCH.value = 0x01
    elif channel == "RA2":
        ADPCH.value = 0x02
    elif channel == "RA3":
        ADPCH.value = 0x03
    elif channel == "RA4":
        ADPCH.value = 0x04
    elif channel == "RA5":
        ADPCH.value = 0x05


@inline
def adc_start(channel: str):
    ADCON0[0] = 1


@inline
def adc_busy() -> uint8:
    return ADCON0[0]


@inline
def adc_read_result() -> uint16:
    lo: uint8 = ADRESL.value
    hi: uint8 = ADRESH.value
    result: uint16 = lo + hi * 256
    return result


@inline
def adc_read(channel: str) -> uint16:
    ADCON0[0] = 1
    while ADCON0[0] == 1:
        pass
    lo: uint8 = ADRESL.value
    hi: uint8 = ADRESH.value
    result: uint16 = lo + hi * 256
    return result
