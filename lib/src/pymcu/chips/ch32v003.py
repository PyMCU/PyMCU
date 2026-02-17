# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# This file is part of the PyMCU Standard Library (pymcu-stdlib).
#
# pymcu-stdlib is free software: you can redistribute it and/or modify
# it under the terms of the GNU General Public License as published by
# the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# pymcu-stdlib is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
# GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with pymcu-stdlib.  If not, see <https://www.gnu.org/licenses/>.
#
# -----------------------------------------------------------------------------
# NOTICE: STRICT COPYLEFT & STATIC LINKING
# -----------------------------------------------------------------------------
# This file contains hardware abstractions (HAL) and register definitions that
# are statically linked (and/or inline expanded) into the final firmware binary by the pymcuc compiler.
#
# UNLIKE standard compiler libraries (e.g., GCC Runtime Library Exception),
# NO EXCEPTION is granted for proprietary use.
#
# If you compile a program that imports this library, the resulting firmware
# binary is considered a "derivative work" of this library under the GPLv3.
# Therefore, any firmware linked against this library must also be released
# under the terms of the GNU GPLv3.
#
# COMMERCIAL LICENSING:
# If you wish to create proprietary (closed-source) firmware using PyMCU,
# you must acquire a Commercial License from the copyright holder.
#
# For licensing inquiries, visit: https://pymcu.org/licensing
# or contact: sales@pymcu.org
#
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

from pymcu.types import ptr, uint8, int32, device_info

# ==========================================
#  Device Memory Configuration
# ==========================================
RAM_START = 0x20000000
RAM_SIZE = 2048

device_info(chip="ch32v003", arch="riscv", ram_size=RAM_SIZE)

# ==========================================
#  Register Definitions (MMIO)
# ==========================================

# RCC
RCC_BASE = 0x40021000
RCC_CTLR   : ptr[int32] = ptr(RCC_BASE + 0x00)
RCC_CFGR0  : ptr[int32] = ptr(RCC_BASE + 0x04)
RCC_INTR   : ptr[int32] = ptr(RCC_BASE + 0x08)
RCC_APB2PRSTR : ptr[int32] = ptr(RCC_BASE + 0x0C)
RCC_APB1PRSTR : ptr[int32] = ptr(RCC_BASE + 0x10)
RCC_AHBENR : ptr[int32] = ptr(RCC_BASE + 0x14)
RCC_APB2ENR : ptr[int32] = ptr(RCC_BASE + 0x18)
RCC_APB1ENR : ptr[int32] = ptr(RCC_BASE + 0x1C)

# GPIO A
GPIOA_BASE = 0x40010800
GPIOA_CFGLR : ptr[int32] = ptr(GPIOA_BASE + 0x00)
GPIOA_INDR  : ptr[int32] = ptr(GPIOA_BASE + 0x04)
GPIOA_OUTDR : ptr[int32] = ptr(GPIOA_BASE + 0x08)
GPIOA_BSHR  : ptr[int32] = ptr(GPIOA_BASE + 0x0C)
GPIOA_BCR   : ptr[int32] = ptr(GPIOA_BASE + 0x10)

# GPIO C
GPIOC_BASE = 0x40011000
GPIOC_CFGLR : ptr[int32] = ptr(GPIOC_BASE + 0x00)
GPIOC_INDR  : ptr[int32] = ptr(GPIOC_BASE + 0x04)
GPIOC_OUTDR : ptr[int32] = ptr(GPIOC_BASE + 0x08)
GPIOC_BSHR  : ptr[int32] = ptr(GPIOC_BASE + 0x0C)
GPIOC_BCR   : ptr[int32] = ptr(GPIOC_BASE + 0x10)

# GPIO D
GPIOD_BASE = 0x40011400
GPIOD_CFGLR : ptr[int32] = ptr(GPIOD_BASE + 0x00)
GPIOD_INDR  : ptr[int32] = ptr(GPIOD_BASE + 0x04)
GPIOD_OUTDR : ptr[int32] = ptr(GPIOD_BASE + 0x08)
GPIOD_BSHR  : ptr[int32] = ptr(GPIOD_BASE + 0x0C)
GPIOD_BCR   : ptr[int32] = ptr(GPIOD_BASE + 0x10)

# SysTick
SYSTICK_BASE = 0xE000E010
SYSTICK_CTLR : ptr[int32] = ptr(SYSTICK_BASE + 0x00)
SYSTICK_SR   : ptr[int32] = ptr(SYSTICK_BASE + 0x04)
SYSTICK_CNT  : ptr[int32] = ptr(SYSTICK_BASE + 0x08)
SYSTICK_CMP  : ptr[int32] = ptr(SYSTICK_BASE + 0x0C)
