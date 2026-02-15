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

from pymcu.types import ptr, uint8

# Device Memory Configuration for PIC18F45K50
# RAM: 2048 bytes total (Access Bank + GPR)
# Access Bank: 0x000-0x05F
# GPR Bank 0: 0x060-0x0FF (160 bytes)
# GPR Banks 1-14: 0x100-0xEFF (additional banks)
RAM_START = 0x060
RAM_SIZE = 2048

# SFRs for PIC18F45K50 (Partial)
PORTA: ptr[uint8] = ptr(0xF80)
PORTB: ptr[uint8] = ptr(0xF81)
PORTC: ptr[uint8] = ptr(0xF82)

TRISA: ptr[uint8] = ptr(0xF92)
TRISB: ptr[uint8] = ptr(0xF93)
TRISC: ptr[uint8] = ptr(0xF94)

LATA:  ptr[uint8] = ptr(0xF89)
LATB:  ptr[uint8] = ptr(0xF8A)
LATC:  ptr[uint8] = ptr(0xF8B)

ANSELA: ptr[uint8] = ptr(0xF38)
ANSELB: ptr[uint8] = ptr(0xF39)
ANSELC: ptr[uint8] = ptr(0xF3A)

# Bits
RA0 = 0; RA1 = 1; RA2 = 2; RA3 = 3; RA4 = 4; RA5 = 5; RA6 = 6; RA7 = 7
RB0 = 0; RB1 = 1; RB2 = 2; RB3 = 3; RB4 = 4; RB5 = 5; RB6 = 6; RB7 = 7
RC0 = 0; RC1 = 1; RC2 = 2; RC6 = 6; RC7 = 7
