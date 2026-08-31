# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------
#
# Raspberry Pi RP2350 (Raspberry Pi Pico 2, dual Cortex-M33).  This definition
# targets single-core (core0) bare-metal ARM operation.  Every peripheral is
# memory-mapped; the ARM (LLVM) codegen backend lowers ptr loads/stores to
# volatile accesses.
#
# Like the RP2040, the RP2350 exposes atomic register aliases: base + 0x1000 =
# XOR, base + 0x2000 = set, base + 0x3000 = clear.  NOTE: peripheral base
# addresses and several SIO register offsets MOVED relative to the RP2040, and
# RP2350 pads power up isolated (PADS bit 8 = ISO must be cleared).

from pymcu.types import ptr, uint32, device_info

# ==========================================
#  Device Memory Configuration
# ==========================================
RAM_START = 0x20000000
RAM_SIZE = 524288              # 512 KB main SRAM (Pico 2)
FLASH_START = 0x10000000       # XIP flash window

device_info(chip="rp2350", arch="arm", ram_size=RAM_SIZE)

# ==========================================
#  RESETS (peripheral reset controller) -- base moved to 0x40020000
# ==========================================
RESETS_BASE = 0x40020000
RESETS_RESET       : ptr[uint32] = ptr(RESETS_BASE + 0x00)
RESETS_RESET_DONE  : ptr[uint32] = ptr(RESETS_BASE + 0x08)
# Atomic aliases of RESETS_RESET.
RESETS_RESET_SET   : ptr[uint32] = ptr(RESETS_BASE + 0x2000 + 0x00)
RESETS_RESET_CLR   : ptr[uint32] = ptr(RESETS_BASE + 0x3000 + 0x00)

# RESETS reset bit positions (RP2350 SVD order: 0=adc .. 28=usbctrl).
RESET_IO_BANK0   = 6
RESET_PADS_BANK0 = 9
RESET_UART0      = 26
RESET_PIO0       = 11
RESET_PIO1       = 12
RESET_PIO2       = 13
RESET_SPI0       = 18
RESET_SPI1       = 19
RESET_I2C0       = 4
RESET_I2C1       = 5
RESET_PWM        = 16
RESET_ADC        = 0
RESET_DMA        = 2

# ==========================================
#  IO_BANK0 (GPIO function select / control) -- base moved to 0x40028000
# ==========================================
IO_BANK0_BASE = 0x40028000
# Per-pin layout unchanged: GPIOx_STATUS (+0x00) and GPIOx_CTRL (+0x04), stride 8.
IO_BANK0_GPIO0_CTRL : ptr[uint32] = ptr(IO_BANK0_BASE + 0x04)

# Function-select values (FUNCSEL is CTRL[4:0]).
GPIO_FUNC_SPI  = 1
GPIO_FUNC_UART = 2
GPIO_FUNC_I2C  = 3
GPIO_FUNC_PWM  = 4
GPIO_FUNC_SIO  = 5
# PIO function select (RP2350 has three PIO blocks).
GPIO_FUNC_PIO0 = 6
GPIO_FUNC_PIO1 = 7
GPIO_FUNC_PIO2 = 8

# ==========================================
#  PADS_BANK0 (pad control) -- base moved to 0x40038000
# ==========================================
PADS_BANK0_BASE = 0x40038000
# VOLTAGE_SELECT at +0x00; per-pin pad register at PADS_BANK0_BASE + 0x04 + 4*n.
PADS_BANK0_GPIO0 : ptr[uint32] = ptr(PADS_BANK0_BASE + 0x04)
PAD_IE  = 6     # input enable bit
PAD_OD  = 7     # output disable bit
PAD_ISO = 8     # isolation bit (resets to 1 on RP2350; must be cleared)

# ==========================================
#  SIO (single-cycle IO; core-local GPIO) -- base same, offsets MOVED
# ==========================================
SIO_BASE = 0xD0000000
SIO_GPIO_IN      : ptr[uint32] = ptr(SIO_BASE + 0x004)
SIO_GPIO_OUT     : ptr[uint32] = ptr(SIO_BASE + 0x010)
SIO_GPIO_OUT_SET : ptr[uint32] = ptr(SIO_BASE + 0x018)
SIO_GPIO_OUT_CLR : ptr[uint32] = ptr(SIO_BASE + 0x020)
SIO_GPIO_OUT_XOR : ptr[uint32] = ptr(SIO_BASE + 0x028)
SIO_GPIO_OE      : ptr[uint32] = ptr(SIO_BASE + 0x030)
SIO_GPIO_OE_SET  : ptr[uint32] = ptr(SIO_BASE + 0x038)
SIO_GPIO_OE_CLR  : ptr[uint32] = ptr(SIO_BASE + 0x040)

# ==========================================
#  UART0 (PL011) -- base moved to 0x40070000 (register offsets unchanged)
# ==========================================
UART0_BASE = 0x40070000
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
#  Clocks (minimal: peripheral clock for UART) -- base moved to 0x40010000
# ==========================================
CLOCKS_BASE = 0x40010000

# ==========================================
#  TIMER0 (free-running 64-bit microsecond counter) -- base moved to 0x400B0000
# ==========================================
TIMER_BASE = 0x400B0000
TIMER_TIMERAWL : ptr[uint32] = ptr(TIMER_BASE + 0x28)   # raw low 32 bits (us)
TIMER_TIMERAWH : ptr[uint32] = ptr(TIMER_BASE + 0x24)   # raw high 32 bits (us)

# ==========================================
#  PIO (Programmable I/O) -- three blocks on RP2350, 4 state machines each
# ==========================================
# Register offsets match the RP2040 PIO; bases are the same plus a third block.
PIO0_BASE = 0x50200000
PIO1_BASE = 0x50300000
PIO2_BASE = 0x50400000
PIO_CTRL       = 0x000
PIO_FSTAT      = 0x004
PIO_TXF0       = 0x010   # +4*sm
PIO_RXF0       = 0x020   # +4*sm
PIO_INSTR_MEM0 = 0x048   # +4*i (32 entries)
PIO_SM0_BASE   = 0x0C8
PIO_SM_STRIDE  = 0x18
PIO_SM_CLKDIV    = 0x00
PIO_SM_EXECCTRL  = 0x04
PIO_SM_SHIFTCTRL = 0x08
PIO_SM_ADDR      = 0x0C
PIO_SM_INSTR     = 0x10
PIO_SM_PINCTRL   = 0x14

# ==========================================
#  SPI (ARM PL022) -- bases moved on RP2350; register offsets unchanged
# ==========================================
SPI0_BASE = 0x40080000
SPI1_BASE = 0x40088000
SPI0_SSPCR0  : ptr[uint32] = ptr(SPI0_BASE + 0x000)
SPI0_SSPCR1  : ptr[uint32] = ptr(SPI0_BASE + 0x004)
SPI0_SSPDR   : ptr[uint32] = ptr(SPI0_BASE + 0x008)
SPI0_SSPSR   : ptr[uint32] = ptr(SPI0_BASE + 0x00C)
SPI0_SSPCPSR : ptr[uint32] = ptr(SPI0_BASE + 0x010)
SSP_SR_TNF = 1
SSP_SR_RNE = 2
SSP_SR_BSY = 4

# ==========================================
#  I2C (Synopsys DW_apb_i2c) -- bases moved; register offsets unchanged
# ==========================================
I2C0_BASE = 0x40090000
I2C1_BASE = 0x40098000
I2C0_IC_CON           : ptr[uint32] = ptr(I2C0_BASE + 0x00)
I2C0_IC_TAR           : ptr[uint32] = ptr(I2C0_BASE + 0x04)
I2C0_IC_DATA_CMD      : ptr[uint32] = ptr(I2C0_BASE + 0x10)
I2C0_IC_ENABLE        : ptr[uint32] = ptr(I2C0_BASE + 0x6C)
I2C0_IC_STATUS        : ptr[uint32] = ptr(I2C0_BASE + 0x70)
I2C0_IC_CLR_TX_ABRT   : ptr[uint32] = ptr(I2C0_BASE + 0x54)
I2C0_IC_TXFLR         : ptr[uint32] = ptr(I2C0_BASE + 0x74)
I2C_CMD_READ    = 0x100
I2C_CMD_STOP    = 0x200
I2C_CMD_RESTART = 0x400
I2C_STATUS_TFNF = 1
I2C_STATUS_TFE  = 2
I2C_STATUS_RFNE = 3

# ==========================================
#  PWM -- bases moved; per-slice layout unchanged (RP2350 has 12 slices)
# ==========================================
PWM_BASE = 0x400A8000
PWM_CH_STRIDE = 0x14
PWM_CH_CSR = 0x00
PWM_CH_DIV = 0x04
PWM_CH_CTR = 0x08
PWM_CH_CC  = 0x0C
PWM_CH_TOP = 0x10

# ==========================================
#  ADC -- base moved; CS / RESULT layout unchanged
# ==========================================
ADC_BASE = 0x400A0000
ADC_CS     : ptr[uint32] = ptr(ADC_BASE + 0x00)
ADC_RESULT : ptr[uint32] = ptr(ADC_BASE + 0x04)
ADC_CS_EN         = 0
ADC_CS_TS_EN      = 1
ADC_CS_START_ONCE = 2
ADC_CS_READY      = 8

# ==========================================
#  DMA -- same base/layout as RP2040 (AHB region)
# ==========================================
DMA_BASE = 0x50000000
DMA_CH_STRIDE      = 0x40
DMA_CH_READ_ADDR   = 0x00
DMA_CH_WRITE_ADDR  = 0x04
DMA_CH_TRANS_COUNT = 0x08
DMA_CH_CTRL_TRIG   = 0x0C
DMA_CTRL_BUSY = 26       # RP2350 CTRL_TRIG BUSY bit (moved from 24)
DMA_CTRL_TREQ_SHIFT = 17  # RP2350 TREQ_SEL field at bits [22:17] (moved from 15)
DMA_CH0_READ_ADDR   : ptr[uint32] = ptr(DMA_BASE + 0x00)
DMA_CH0_WRITE_ADDR  : ptr[uint32] = ptr(DMA_BASE + 0x04)
DMA_CH0_TRANS_COUNT : ptr[uint32] = ptr(DMA_BASE + 0x08)
DMA_CH0_CTRL_TRIG   : ptr[uint32] = ptr(DMA_BASE + 0x0C)


# The CYW43439 register map used to sit here. It described a second vendor's part, not
# this MCU, and the Pico W carries the same CYW43439 on a different MCU, so keeping it
# in a chip file meant writing thirty-five numbers twice. It lives in
# `pymcu/hal/cyw43_regs.py` now, with the four board pins beside it.
#
# What stayed here is what IS this chip: the GPIO, pad and reset registers the driver
# bit-bangs the bus with. Ten of those twelve hold a different value on the RP2040.
