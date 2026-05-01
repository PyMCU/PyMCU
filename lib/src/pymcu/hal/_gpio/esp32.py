# SPDX-License-Identifier: MIT
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
# Licensed under the MIT License. See LICENSE for details.
#
# hal/_gpio/esp32.py -- GPIO HAL for ESP32 (LX6 core)
#
# ESP32 GPIO register map (base = 0x3FF44000):
#   GPIO_OUT_W1TS_REG    = 0x3FF44008  -- write 1 to set output bits
#   GPIO_OUT_W1TC_REG    = 0x3FF4400C  -- write 1 to clear output bits
#   GPIO_ENABLE_W1TS_REG = 0x3FF44024  -- enable output for GPIO0-31
#   GPIO_ENABLE_W1TC_REG = 0x3FF44028  -- disable output for GPIO0-31
#   GPIO_IN_REG          = 0x3FF4403C  -- read GPIO0-31 input levels
#
# Only GPIO 0-31 are supported here (simple 32-bit registers).

from pymcu.types import uint32, inline

GPIO_OUT_W1TS: uint32 = 0x3FF44008
GPIO_OUT_W1TC: uint32 = 0x3FF4400C
GPIO_ENABLE_W1TS: uint32 = 0x3FF44024
GPIO_ENABLE_W1TC: uint32 = 0x3FF44028
GPIO_IN: uint32 = 0x3FF4403C

OUT: uint32 = 0
IN:  uint32 = 1


@inline
def gpio_set_mode(pin: uint32, mode: uint32):
    # mode=0 (OUT): enable output for pin; mode=1 (IN): disable output.
    if mode == OUT:
        mask: uint32 = 1 << pin
        ptr_en: uint32 = GPIO_ENABLE_W1TS
        asm("s32i a2, a3, 0")
    else:
        mask: uint32 = 1 << pin
        ptr_dis: uint32 = GPIO_ENABLE_W1TC
        asm("s32i a2, a3, 0")


@inline
def gpio_write(pin: uint32, value: uint32):
    mask: uint32 = 1 << pin
    if value != 0:
        ptr_set: uint32 = GPIO_OUT_W1TS
        asm("s32i a2, a3, 0")
    else:
        ptr_clr: uint32 = GPIO_OUT_W1TC
        asm("s32i a2, a3, 0")


@inline
def gpio_read(pin: uint32) -> uint32:
    val: uint32 = 0
    asm("l32i a2, a3, 0")
    result: uint32 = (val >> pin) & 1
    return result
