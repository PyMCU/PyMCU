# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# -----------------------------------------------------------------------------
#
# CYW43439 register map -- pymcu.hal.cyw43_regs
#
# These describe the CYW43439 WiFi/BT part, not the microcontroller talking to it.
# They lived in `chips/rp2350.py` because the Pico 2 W was the first board to carry
# one, which put a second vendor's register map inside an MCU's file: a Pico W has
# the same CYW43439 and a different MCU, so the numbers would have had to be written
# twice. This project has already paid for a register map copied from a sibling part
# four times, and twenty numbers by hand is how that happens again.
#
# Nothing here is chip-dependent, so there is no conditional import and no per-chip
# variant. What IS chip-dependent -- the GPIO and reset registers of whichever MCU
# bit-bangs the bus -- stays in that MCU's chip file and is selected in the driver.
#
# The board wiring below is separate for the same reason in the other direction: the
# four pins are a property of the BOARD, and they happen to be identical on the Pico W
# and the Pico 2 W. If a third board wires the part differently, that is the one block
# here that has to grow a condition.

# ── board wiring: the four GPIOs the CYW43439 hangs off ──
# Identical on the Pico W and the Pico 2 W (pico_w.h and pico2_w.h agree).
WL_REG_ON = 23    # power/reset gate (active high)
WL_DATA   = 24    # shared data out/in (also WL_HOST_WAKE when idle)
WL_CS     = 25    # chip select (active low)
WL_CLK    = 29    # clock

# ── gSPI F0 (bus) control registers ──
SPI_BUS_CONTROL        = 0x00   # write WORD_LENGTH_32 -> switch 16b-swapped -> 32b-LE
SPI_RESPONSE_DELAY     = 0x01
SPI_STATUS_ENABLE      = 0x02
SPI_INTERRUPT_REGISTER = 0x04
SPI_STATUS_REGISTER    = 0x08
SPI_READ_TEST_REGISTER = 0x14   # reads back 0xFEEDBEAD ("chip alive")
SPI_TEST_PATTERN       = 0xFEEDBEAD

# SPI_BUS_CONTROL bits: 32-bit word length + big-endian
SPI_WORD_LENGTH_32 = 0x01
SPI_ENDIAN_BIG     = 0x10

# ── gSPI functions ──
GSPI_F0_BUS       = 0   # bus control regs
GSPI_F1_BACKPLANE = 1   # windowed chip memory + SDIO control regs
GSPI_F2_WLAN      = 2   # SDPCM data plane

# ── F1 backplane SDIO control registers (offset >= 0x10000) ──
SDIO_BACKPLANE_ADDR_LOW  = 0x1000A
SDIO_BACKPLANE_ADDR_MID  = 0x1000B
SDIO_BACKPLANE_ADDR_HIGH = 0x1000C
SDIO_CHIP_CLOCK_CSR      = 0x1000E
SDIO_SLEEP_CSR           = 0x1001F

# CHIP_CLOCK_CSR bits
SBSDIO_ALP_AVAIL_REQ = 0x08
SBSDIO_HT_AVAIL_REQ  = 0x10
SBSDIO_FORCE_HT      = 0x02
SBSDIO_ALP_AVAIL     = 0x40
SBSDIO_HT_AVAIL      = 0x80

# ── backplane chip addresses ──
BP_CHIPCOMMON      = 0x18000000
BP_SDIO_INT_STATUS = 0x18002020
BP_WLAN_ARMCORE    = 0x18103800   # WLAN ARM core wrapper; +0x800 = AI_RESETCTRL
AI_RESETCTRL_OFFSET = 0x800
AIRC_RESET          = 0x01

# ── gSPI F0 SPI_STATUS_REGISTER bits (composed by the chip on read) ──
STATUS_F2_RX_READY       = 0x00000020   # WLAN core up, F2 ready
STATUS_F2_PKT_AVAILABLE  = 0x00000100   # an SDPCM packet is queued for the host
STATUS_F2_PKT_LEN_SHIFT  = 9            # packet length lives at bits [20:9]
STATUS_F2_PKT_LEN_MASK   = 0x0FFF
