from pymcu.types import uint8, uint16, inline
from pymcu.chips.pic16f877a import ADCON0, ADCON1, ADRESH, ADRESL


@inline
def adc_init(channel: str):
    ADCON1.value = 0x80
    if channel == "RA0":
        ADCON0.value = 0x41
    elif channel == "RA1":
        ADCON0.value = 0x49
    elif channel == "RA2":
        ADCON0.value = 0x51
    elif channel == "RA3":
        ADCON0.value = 0x59
    elif channel == "RA5":
        ADCON0.value = 0x61
    elif channel == "RE0":
        ADCON0.value = 0x69
    elif channel == "RE1":
        ADCON0.value = 0x71
    elif channel == "RE2":
        ADCON0.value = 0x79


@inline
def adc_start(channel: str):
    ADCON0[2] = 1


@inline
def adc_busy() -> uint8:
    return ADCON0[2]


@inline
def adc_read_result() -> uint16:
    lo: uint8 = ADRESL.value
    hi: uint8 = ADRESH.value
    result: uint16 = lo + hi * 256
    return result


@inline
def adc_read(channel: str) -> uint16:
    ADCON0[2] = 1
    while ADCON0[2] == 1:
        pass
    lo: uint8 = ADRESL.value
    hi: uint8 = ADRESH.value
    result: uint16 = lo + hi * 256
    return result
