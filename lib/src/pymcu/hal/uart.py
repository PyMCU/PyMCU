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

from pymcu.types import uint8, uint16, inline, const
from pymcu.chips import __CHIP__

class UART:
    @inline
    def __init__(self: uint8, baud: const[uint16] = 9600):
        match __CHIP__.arch:
            case "avr":
                from pymcu.hal._uart.avr import uart_init
                uart_init(baud)
            case "pic14":
                from pymcu.hal._uart.pic14 import uart_init
                uart_init(baud)
            case "pic18":
                from pymcu.hal._uart.pic18 import uart_init
                uart_init(baud)

    @inline
    def write(self: uint8, data: uint8):
        match __CHIP__.arch:
            case "avr":
                from pymcu.hal._uart.avr import uart_write
                uart_write(data)
            case "pic14":
                from pymcu.hal._uart.pic14 import uart_write
                uart_write(data)
            case "pic18":
                from pymcu.hal._uart.pic18 import uart_write
                uart_write(data)

    @inline
    def read(self: uint8) -> uint8:
        match __CHIP__.arch:
            case "avr":
                from pymcu.hal._uart.avr import uart_read
                return uart_read()
            case "pic14":
                from pymcu.hal._uart.pic14 import uart_read
                return uart_read()
            case "pic18":
                from pymcu.hal._uart.pic18 import uart_read
                return uart_read()
