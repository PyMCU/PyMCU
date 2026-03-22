# -----------------------------------------------------------------------------
# Whipsnake Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the Whipsnake Project Authors
#
# This file is part of the Whipsnake Standard Library (whipsnake-stdlib).
#
# whipsnake-stdlib is free software: you can redistribute it and/or modify
# it under the terms of the GNU General Public License as published by
# the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# whipsnake-stdlib is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
# GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with whipsnake-stdlib.  If not, see <https://www.gnu.org/licenses/>.
#
# -----------------------------------------------------------------------------
# NOTICE: STRICT COPYLEFT & STATIC LINKING
# -----------------------------------------------------------------------------
# This file contains hardware abstractions (HAL) and register definitions that
# are statically linked (and/or inline expanded) into the final firmware binary by the whipc compiler.
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
# If you wish to create proprietary (closed-source) firmware using Whipsnake,
# you must acquire a Commercial License from the copyright holder.
#
# For licensing inquiries, visit: https://whipsnake.org/licensing
# or contact: sales@whipsnake.org
#
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

from whipsnake.types import ptr, uint8, device_info

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
