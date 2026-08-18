# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# CH32V203 -- QingKe V4B core (RV32IMAC), 64K flash, 20K RAM.
#
# The peripheral layout is the familiar STM32F1 one, like the CH32V003, but the
# ports are 16 pins wide: CFGLR configures pins 0-7 and CFGHR pins 8-15.
# -----------------------------------------------------------------------------

from pymcu.types import ptr, int32, device_info

# ==========================================
#  Device Memory Configuration
# ==========================================
RAM_START = 0x20000000
RAM_SIZE = 20480
# 64 KB, ch32fun 1e92c65: the v20x MCU_PACKAGE 1 variant, the one
# whose RAM is 20K -- which is the RAM_SIZE this file already declares
FLASH_SIZE = 65536

device_info(chip="ch32v203", arch="riscv", ram_size=RAM_SIZE, flash_size=FLASH_SIZE)

# ==========================================
#  Register Definitions (MMIO)
# ==========================================

# RCC
RCC_BASE = 0x40021000
RCC_CTLR      : ptr[int32] = ptr(RCC_BASE + 0x00)
RCC_CFGR0     : ptr[int32] = ptr(RCC_BASE + 0x04)
RCC_INTR      : ptr[int32] = ptr(RCC_BASE + 0x08)
RCC_APB2PRSTR : ptr[int32] = ptr(RCC_BASE + 0x0C)
RCC_APB1PRSTR : ptr[int32] = ptr(RCC_BASE + 0x10)
RCC_AHBPCENR  : ptr[int32] = ptr(RCC_BASE + 0x14)
RCC_APB2PCENR : ptr[int32] = ptr(RCC_BASE + 0x18)
RCC_APB1PCENR : ptr[int32] = ptr(RCC_BASE + 0x1C)

# GPIO A
GPIOA_BASE = 0x40010800
GPIOA_CFGLR : ptr[int32] = ptr(GPIOA_BASE + 0x00)
GPIOA_CFGHR : ptr[int32] = ptr(GPIOA_BASE + 0x04)
GPIOA_INDR  : ptr[int32] = ptr(GPIOA_BASE + 0x08)
GPIOA_OUTDR : ptr[int32] = ptr(GPIOA_BASE + 0x0C)
GPIOA_BSHR  : ptr[int32] = ptr(GPIOA_BASE + 0x10)
GPIOA_BCR   : ptr[int32] = ptr(GPIOA_BASE + 0x14)

# GPIO B
GPIOB_BASE = 0x40010C00
GPIOB_CFGLR : ptr[int32] = ptr(GPIOB_BASE + 0x00)
GPIOB_CFGHR : ptr[int32] = ptr(GPIOB_BASE + 0x04)
GPIOB_INDR  : ptr[int32] = ptr(GPIOB_BASE + 0x08)
GPIOB_OUTDR : ptr[int32] = ptr(GPIOB_BASE + 0x0C)
GPIOB_BSHR  : ptr[int32] = ptr(GPIOB_BASE + 0x10)
GPIOB_BCR   : ptr[int32] = ptr(GPIOB_BASE + 0x14)

# GPIO C
GPIOC_BASE = 0x40011000
GPIOC_CFGLR : ptr[int32] = ptr(GPIOC_BASE + 0x00)
GPIOC_CFGHR : ptr[int32] = ptr(GPIOC_BASE + 0x04)
GPIOC_INDR  : ptr[int32] = ptr(GPIOC_BASE + 0x08)
GPIOC_OUTDR : ptr[int32] = ptr(GPIOC_BASE + 0x0C)
GPIOC_BSHR  : ptr[int32] = ptr(GPIOC_BASE + 0x10)
GPIOC_BCR   : ptr[int32] = ptr(GPIOC_BASE + 0x14)

# GPIO D
GPIOD_BASE = 0x40011400
GPIOD_CFGLR : ptr[int32] = ptr(GPIOD_BASE + 0x00)
GPIOD_CFGHR : ptr[int32] = ptr(GPIOD_BASE + 0x04)
GPIOD_INDR  : ptr[int32] = ptr(GPIOD_BASE + 0x08)
GPIOD_OUTDR : ptr[int32] = ptr(GPIOD_BASE + 0x0C)
GPIOD_BSHR  : ptr[int32] = ptr(GPIOD_BASE + 0x10)
GPIOD_BCR   : ptr[int32] = ptr(GPIOD_BASE + 0x14)

# SysTick
SYSTICK_BASE = 0xE000F000
SYSTICK_CTLR : ptr[int32] = ptr(SYSTICK_BASE + 0x00)
SYSTICK_SR   : ptr[int32] = ptr(SYSTICK_BASE + 0x04)
SYSTICK_CNT  : ptr[int32] = ptr(SYSTICK_BASE + 0x08)
SYSTICK_CMP  : ptr[int32] = ptr(SYSTICK_BASE + 0x10)
