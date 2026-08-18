# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
from pymcu.types import uint8, uint16, inline
from pymcu.chips import __FREQ__
from pymcu.chips.pic18f45k50 import (
    ADCON0, ADCON1, ADCON2, ADRESH, ADRESL, ANSELA, TRISA,
)


@inline
def adc_channel_adcon0(channel: str) -> uint8:
    if channel == "RA0" or channel == "AN0":
        return 0x01
    elif channel == "RA1" or channel == "AN1":
        return 0x05
    elif channel == "RA2" or channel == "AN2":
        return 0x09
    elif channel == "RA3" or channel == "AN3":
        return 0x0D
    elif channel == "RA5" or channel == "AN4":
        return 0x11
    else:
        raise NotImplementedError("ADC channel not available on PIC18F45K50")


@inline
def adc_adcon2() -> uint8:
    match __FREQ__:
        case 1_000_000:
            return 0xA8
        case 4_000_000:
            return 0xAC
        case 8_000_000:
            return 0xA9
        case 16_000_000:
            return 0xAD
        case 48_000_000:
            return 0xAE
        case _:
            return 0xAE


@inline
def adc_pin_analog(channel: str):
    if channel == "RA0" or channel == "AN0":
        ANSELA[0] = 1
        TRISA[0] = 1
    elif channel == "RA1" or channel == "AN1":
        ANSELA[1] = 1
        TRISA[1] = 1
    elif channel == "RA2" or channel == "AN2":
        ANSELA[2] = 1
        TRISA[2] = 1
    elif channel == "RA3" or channel == "AN3":
        ANSELA[3] = 1
        TRISA[3] = 1
    elif channel == "RA5" or channel == "AN4":
        ANSELA[5] = 1
        TRISA[5] = 1


@inline
def adc_init(channel: str):
    adc_pin_analog(channel)
    ADCON1.value = 0x00
    ADCON2.value = adc_adcon2()
    ADCON0.value = adc_channel_adcon0(channel)


@inline
def adc_select(channel: str):
    ADCON0.value = adc_channel_adcon0(channel)


@inline
def adc_start(channel: str):
    ADCON0.value = adc_channel_adcon0(channel)
    ADCON0[1] = 1


@inline
def adc_busy() -> uint8:
    return ADCON0[1]


@inline
def adc_read_result() -> uint16:
    lo: uint8 = ADRESL.value
    hi: uint8 = ADRESH.value
    result: uint16 = lo + hi * 256
    return result


@inline
def adc_read(channel: str) -> uint16:
    ADCON0.value = adc_channel_adcon0(channel)
    ADCON0[1] = 1
    while ADCON0[1] == 1:
        pass
    lo: uint8 = ADRESL.value
    hi: uint8 = ADRESH.value
    result: uint16 = lo + hi * 256
    return result


@inline
def adc_read_u16(channel: str) -> uint16:
    ADCON0.value = adc_channel_adcon0(channel)
    ADCON0[1] = 1
    while ADCON0[1] == 1:
        pass
    lo: uint8 = ADRESL.value
    hi: uint8 = ADRESH.value
    result: uint16 = (lo + hi * 256) * 64
    return result
