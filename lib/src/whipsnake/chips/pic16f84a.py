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

# ==========================================
#  Device Memory Configuration
# ==========================================
# PIC16F84A has 68 bytes of RAM (small device for testing limits)
# RAM: 0x0C-0x4F (Bank 0: 0x0C-0x2F, Bank 1: 0x8C-0xAF mapped to 0x0C-0x2F)
# We use 0x20 as start to align with general purpose registers
RAM_START = 0x20
RAM_SIZE = 68

device_info(chip="pic16f84a", arch="pic14", ram_size=RAM_SIZE)

# ==========================================
#  Register Definitions
# ==========================================

# Special Function Registers (Bank 0)
INDF:    ptr[uint8] = ptr(0x00)
TMR0:    ptr[uint8] = ptr(0x01)
PCL:     ptr[uint8] = ptr(0x02)
STATUS:  ptr[uint8] = ptr(0x03)
FSR:     ptr[uint8] = ptr(0x04)
PORTA:   ptr[uint8] = ptr(0x05)
PORTB:   ptr[uint8] = ptr(0x06)
EEDATA:  ptr[uint8] = ptr(0x08)
EEADR:   ptr[uint8] = ptr(0x09)
PCLATH:  ptr[uint8] = ptr(0x0A)
INTCON:  ptr[uint8] = ptr(0x0B)

# Special Function Registers (Bank 1)
OPTION_REG: ptr[uint8] = ptr(0x81)
TRISA:      ptr[uint8] = ptr(0x85)
TRISB:      ptr[uint8] = ptr(0x86)
EECON1:     ptr[uint8] = ptr(0x88)
EECON2:     ptr[uint8] = ptr(0x89)

# Status Register Bits
C = 0    # Carry bit
DC = 1   # Digit carry bit
Z = 2    # Zero bit
PD = 3   # Power-down bit
TO = 4   # Time-out bit
RP0 = 5  # Register bank select bit 0
RP1 = 6  # Register bank select bit 1
IRP = 7  # Indirect addressing register bank select

# PORTA Bits
RA0 = 0
RA1 = 1
RA2 = 2
RA3 = 3
RA4 = 4

# PORTB Bits
RB0 = 0
RB1 = 1
RB2 = 2
RB3 = 3
RB4 = 4
RB5 = 5
RB6 = 6
RB7 = 7

# INTCON Bits
RBIF = 0   # RB Port Change Interrupt Flag
INTF = 1   # External Interrupt Flag
T0IF = 2   # TMR0 Overflow Interrupt Flag
RBIE = 3   # RB Port Change Interrupt Enable
INTE = 4   # External Interrupt Enable
T0IE = 5   # TMR0 Overflow Interrupt Enable
EEIE = 6   # EEPROM Write Complete Interrupt Enable
GIE = 7    # Global Interrupt Enable
