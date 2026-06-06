# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# Raspberry Pi RP2040 (dual Cortex-M0+).  This definition targets single-core
# (core0) bare-metal operation.  Every peripheral is memory-mapped; the codegen
# backend (pymcuc-rp2040) lowers ptr loads/stores to volatile LLVM IR accesses.
#
# RP2040 exposes atomic register aliases: writing to base + 0x1000 performs an
# XOR, base + 0x2000 a set, and base + 0x3000 a clear of the underlying
# register without a read-modify-write.  The HAL uses the SET/CLR aliases of
# the SIO GPIO_OUT / GPIO_OE registers for single-cycle pin toggling.

from pymcu.types import ptr, uint32, device_info

# ==========================================
#  Device Memory Configuration
# ==========================================
RAM_START = 0x20000000
RAM_SIZE = 270336              # 264 KB SRAM (6 banks)
FLASH_START = 0x10000000       # XIP flash window

device_info(chip="rp2040", arch="rp2040", ram_size=RAM_SIZE)

# ==========================================
#  RESETS (peripheral reset controller)
# ==========================================
RESETS_BASE = 0x4000C000
RESETS_RESET       : ptr[uint32] = ptr(RESETS_BASE + 0x00)
RESETS_RESET_DONE  : ptr[uint32] = ptr(RESETS_BASE + 0x08)
# Atomic aliases of RESETS_RESET.
RESETS_RESET_SET   : ptr[uint32] = ptr(RESETS_BASE + 0x2000 + 0x00)
RESETS_RESET_CLR   : ptr[uint32] = ptr(RESETS_BASE + 0x3000 + 0x00)

# RESETS reset bit positions (subset used by the MVP HAL).
RESET_IO_BANK0   = 5
RESET_PADS_BANK0 = 8
RESET_UART0      = 22

# ==========================================
#  IO_BANK0 (GPIO function select / control)
# ==========================================
IO_BANK0_BASE = 0x40014000
# Per-pin layout: each pin has GPIOx_STATUS (+0x00) and GPIOx_CTRL (+0x04),
# stride 8.  GPIOn_CTRL = IO_BANK0_BASE + 8*n + 0x04.  FUNCSEL is CTRL[4:0].
IO_BANK0_GPIO0_CTRL : ptr[uint32] = ptr(IO_BANK0_BASE + 0x04)

# Function-select values.
GPIO_FUNC_UART = 2
GPIO_FUNC_SIO  = 5

# ==========================================
#  PADS_BANK0 (pad control: input enable, drive, pulls)
# ==========================================
PADS_BANK0_BASE = 0x4001C000
# Per-pin pad register at PADS_BANK0_BASE + 0x04 + 4*n.
PADS_BANK0_GPIO0 : ptr[uint32] = ptr(PADS_BANK0_BASE + 0x04)
PAD_IE = 6     # input enable bit
PAD_OD = 7     # output disable bit

# ==========================================
#  SIO (single-cycle IO; core-local GPIO)
# ==========================================
SIO_BASE = 0xD0000000
SIO_GPIO_IN      : ptr[uint32] = ptr(SIO_BASE + 0x004)
SIO_GPIO_OUT     : ptr[uint32] = ptr(SIO_BASE + 0x010)
SIO_GPIO_OUT_SET : ptr[uint32] = ptr(SIO_BASE + 0x014)
SIO_GPIO_OUT_CLR : ptr[uint32] = ptr(SIO_BASE + 0x018)
SIO_GPIO_OUT_XOR : ptr[uint32] = ptr(SIO_BASE + 0x01C)
SIO_GPIO_OE      : ptr[uint32] = ptr(SIO_BASE + 0x020)
SIO_GPIO_OE_SET  : ptr[uint32] = ptr(SIO_BASE + 0x024)
SIO_GPIO_OE_CLR  : ptr[uint32] = ptr(SIO_BASE + 0x028)

# ==========================================
#  UART0 (PL011)
# ==========================================
UART0_BASE = 0x40034000
UART0_DR    : ptr[uint32] = ptr(UART0_BASE + 0x000)   # data
UART0_FR    : ptr[uint32] = ptr(UART0_BASE + 0x018)   # flag (TXFF bit5, RXFE bit4, BUSY bit3)
UART0_IBRD  : ptr[uint32] = ptr(UART0_BASE + 0x024)   # integer baud divisor
UART0_FBRD  : ptr[uint32] = ptr(UART0_BASE + 0x028)   # fractional baud divisor
UART0_LCR_H : ptr[uint32] = ptr(UART0_BASE + 0x02C)   # line control (WLEN, FEN)
UART0_CR    : ptr[uint32] = ptr(UART0_BASE + 0x030)   # control (UARTEN, TXE, RXE)

UART_FR_RXFE = 4    # receive FIFO empty
UART_FR_TXFF = 5    # transmit FIFO full
UART_FR_BUSY = 3    # transmitter busy

# ==========================================
#  Clocks (minimal: peripheral clock for UART)
# ==========================================
CLOCKS_BASE = 0x40008000
