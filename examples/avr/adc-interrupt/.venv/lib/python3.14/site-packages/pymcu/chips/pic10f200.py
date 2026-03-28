# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

from pymcu.types import ptr, uint8, device_info

# Device Memory Configuration for PIC10F200
# RAM: 16 bytes (0x08-0x17 in Bank 0)
RAM_START = 0x08
RAM_SIZE = 16

device_info(chip="pic10f200", arch="pic12", ram_size=RAM_SIZE)

# PIC10F200 Registers
INDF:    ptr[uint8] = ptr(0x00)
TMR0:    ptr[uint8] = ptr(0x01)
PCL:     ptr[uint8] = ptr(0x02)
STATUS:  ptr[uint8] = ptr(0x03)
FSR:     ptr[uint8] = ptr(0x04)
OSCCAL:  ptr[uint8] = ptr(0x05)
GPIO:    ptr[uint8] = ptr(0x06)

# TRIS and OPTION are special on this chip (write-only)
# But our compiler maps these addresses to TRIS/OPTION instructions
OPTION:  ptr[uint8] = ptr(0x81)
TRISGPIO: ptr[uint8] = ptr(0x86)

# Bits
GP0 = 0
GP1 = 1
GP2 = 2
GP3 = 3
