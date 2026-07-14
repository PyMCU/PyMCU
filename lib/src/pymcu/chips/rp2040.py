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

device_info(chip="rp2040", arch="arm", ram_size=RAM_SIZE)

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
RESET_PIO0       = 10
RESET_PIO1       = 11
RESET_SPI0       = 16
RESET_SPI1       = 17
RESET_I2C0       = 3
RESET_I2C1       = 4
RESET_PWM        = 14
RESET_ADC        = 0
RESET_DMA        = 2

# ==========================================
#  IO_BANK0 (GPIO function select / control)
# ==========================================
IO_BANK0_BASE = 0x40014000
# Per-pin layout: each pin has GPIOx_STATUS (+0x00) and GPIOx_CTRL (+0x04),
# stride 8.  GPIOn_CTRL = IO_BANK0_BASE + 8*n + 0x04.  FUNCSEL is CTRL[4:0].
IO_BANK0_GPIO0_CTRL : ptr[uint32] = ptr(IO_BANK0_BASE + 0x04)

# Function-select values.
GPIO_FUNC_SPI  = 1
GPIO_FUNC_UART = 2
GPIO_FUNC_I2C  = 3
GPIO_FUNC_PWM  = 4
GPIO_FUNC_SIO  = 5
GPIO_FUNC_PIO0 = 6
GPIO_FUNC_PIO1 = 7

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

# ==========================================
#  TIMER (free-running 64-bit microsecond counter)
# ==========================================
# A single 1 MHz counter shared by the whole chip. TIMERAWL is the raw low
# 32 bits with no read-latching side effect, so it is safe to poll directly
# in a busy-wait. Polling it makes delays cycle-accurate and immune to CPU
# clock / pipeline / instruction-count variance (unlike a calibrated loop).
TIMER_BASE = 0x40054000
TIMER_TIMERAWL : ptr[uint32] = ptr(TIMER_BASE + 0x28)   # raw low 32 bits (us)
TIMER_TIMERAWH : ptr[uint32] = ptr(TIMER_BASE + 0x24)   # raw high 32 bits (us)

# ==========================================
#  PIO (Programmable I/O) -- two blocks, 4 state machines each
# ==========================================
# Register offsets are identical for both blocks; only the base differs.
PIO0_BASE = 0x50200000
PIO1_BASE = 0x50300000
# Block-level registers.
PIO_CTRL       = 0x000   # SM_ENABLE[3:0], SM_RESTART[7:4], CLKDIV_RESTART[11:8]
PIO_FSTAT      = 0x004   # TXEMPTY[3:0], TXFULL[11:8], RXEMPTY[19:16], RXFULL[27:24]
PIO_TXF0       = 0x010   # +4*sm: TX FIFO write
PIO_RXF0       = 0x020   # +4*sm: RX FIFO read
PIO_INSTR_MEM0 = 0x048   # +4*i: 32-entry instruction memory
# Per-state-machine register block.
PIO_SM0_BASE   = 0x0C8
PIO_SM_STRIDE  = 0x18
PIO_SM_CLKDIV    = 0x00  # FRAC[15:8], INT[31:16]
PIO_SM_EXECCTRL  = 0x04  # WRAP_BOTTOM[11:7], WRAP_TOP[16:12], SIDE_PINDIR[29], SIDE_EN[30]
PIO_SM_SHIFTCTRL = 0x08  # AUTOPUSH[16], AUTOPULL[17], IN_SHIFTDIR[18], OUT_SHIFTDIR[19],
                         # PUSH_THRESH[24:20], PULL_THRESH[29:25], FJOIN_TX[30], FJOIN_RX[31]
PIO_SM_ADDR      = 0x0C  # current PC (RO)
PIO_SM_INSTR     = 0x10  # execute an instruction immediately
PIO_SM_PINCTRL   = 0x14  # OUT_BASE[4:0], SET_BASE[9:5], SIDESET_BASE[14:10], IN_BASE[19:15],
                         # OUT_COUNT[25:20], SET_COUNT[28:26], SIDESET_COUNT[31:29]

# ==========================================
#  SPI (ARM PL022) -- SPI0 / SPI1
# ==========================================
SPI0_BASE = 0x4003C000
SPI1_BASE = 0x40040000
SPI0_SSPCR0  : ptr[uint32] = ptr(SPI0_BASE + 0x000)   # DSS[3:0], FRF[5:4], SPO[6], SPH[7], SCR[15:8]
SPI0_SSPCR1  : ptr[uint32] = ptr(SPI0_BASE + 0x004)   # LBM[0], SSE[1], MS[2], SOD[3]
SPI0_SSPDR   : ptr[uint32] = ptr(SPI0_BASE + 0x008)   # data
SPI0_SSPSR   : ptr[uint32] = ptr(SPI0_BASE + 0x00C)   # TFE[0], TNF[1], RNE[2], RFF[3], BSY[4]
SPI0_SSPCPSR : ptr[uint32] = ptr(SPI0_BASE + 0x010)   # clock prescale divisor (even, 2-254)
SSP_SR_TNF = 1   # transmit FIFO not full
SSP_SR_RNE = 2   # receive FIFO not empty
SSP_SR_BSY = 4   # busy

# ==========================================
#  I2C (Synopsys DW_apb_i2c) -- I2C0 / I2C1
# ==========================================
I2C0_BASE = 0x40044000
I2C1_BASE = 0x40048000
I2C0_IC_CON           : ptr[uint32] = ptr(I2C0_BASE + 0x00)   # master/speed/restart/slave-dis
I2C0_IC_TAR           : ptr[uint32] = ptr(I2C0_BASE + 0x04)   # target address
I2C0_IC_DATA_CMD      : ptr[uint32] = ptr(I2C0_BASE + 0x10)   # data[7:0], RD[8], STOP[9], RESTART[10]
I2C0_IC_ENABLE        : ptr[uint32] = ptr(I2C0_BASE + 0x6C)   # ENABLE[0]
I2C0_IC_STATUS        : ptr[uint32] = ptr(I2C0_BASE + 0x70)   # TFNF[1], TFE[2], RFNE[3]
I2C0_IC_CLR_TX_ABRT   : ptr[uint32] = ptr(I2C0_BASE + 0x54)
I2C0_IC_TXFLR         : ptr[uint32] = ptr(I2C0_BASE + 0x74)
# IC_DATA_CMD command bits.
I2C_CMD_READ    = 0x100
I2C_CMD_STOP    = 0x200
I2C_CMD_RESTART = 0x400
I2C_STATUS_TFNF = 1   # tx FIFO not full
I2C_STATUS_TFE  = 2   # tx FIFO empty
I2C_STATUS_RFNE = 3   # rx FIFO not empty

# ==========================================
#  PWM -- 8 slices, 2 channels (A/B) each; slice stride 0x14
# ==========================================
PWM_BASE = 0x40050000
PWM_CH_STRIDE = 0x14
PWM_CH_CSR = 0x00   # EN[0], PH_CORRECT[1], A_INV[2], B_INV[3], DIVMODE[5:4]
PWM_CH_DIV = 0x04   # FRAC[3:0], INT[11:4]
PWM_CH_CTR = 0x08   # counter
PWM_CH_CC  = 0x0C   # compare A[15:0], B[31:16]
PWM_CH_TOP = 0x10   # wrap value

# ==========================================
#  ADC -- 5-channel 12-bit SAR; channel 4 = internal temperature
# ==========================================
ADC_BASE = 0x4004C000
ADC_CS     : ptr[uint32] = ptr(ADC_BASE + 0x00)   # EN[0], TS_EN[1], START_ONCE[2], READY[8], AINSEL[14:12]
ADC_RESULT : ptr[uint32] = ptr(ADC_BASE + 0x04)   # 12-bit conversion result
ADC_CS_EN         = 0
ADC_CS_TS_EN      = 1
ADC_CS_START_ONCE = 2
ADC_CS_READY      = 8

# ==========================================
#  DMA -- 12 channels, per-channel register block, stride 0x40
# ==========================================
DMA_BASE = 0x50000000
DMA_CH_STRIDE      = 0x40
DMA_CH_READ_ADDR   = 0x00
DMA_CH_WRITE_ADDR  = 0x04
DMA_CH_TRANS_COUNT = 0x08
DMA_CH_CTRL_TRIG   = 0x0C   # EN[0], DATA_SIZE[3:2], INCR_READ[4], INCR_WRITE[5],
                            # CHAIN_TO[14:11], TREQ_SEL[20:15], BUSY[24]
DMA_CTRL_BUSY = 24       # CTRL_TRIG BUSY bit
DMA_CTRL_TREQ_SHIFT = 15  # TREQ_SEL field at bits [20:15] (0x3F = permanent/unpaced)
# Channel 0 registers as fixed module-level pointers (the common single-channel
# case): fixed addresses fold to constants like every other peripheral register.
DMA_CH0_READ_ADDR   : ptr[uint32] = ptr(DMA_BASE + 0x00)
DMA_CH0_WRITE_ADDR  : ptr[uint32] = ptr(DMA_BASE + 0x04)
DMA_CH0_TRANS_COUNT : ptr[uint32] = ptr(DMA_BASE + 0x08)
DMA_CH0_CTRL_TRIG   : ptr[uint32] = ptr(DMA_BASE + 0x0C)
