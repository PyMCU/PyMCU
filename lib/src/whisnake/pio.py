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
# For licensing inquiries, visit: https://whisnake.org/licensing
# or contact: sales@whisnake.org
#
# -----------------------------------------------------------------------------
# SAFETY WARNING / HIGH RISK ACTIVITIES:
# THE SOFTWARE IS NOT DESIGNED, MANUFACTURED, OR INTENDED FOR USE IN HAZARDOUS
# ENVIRONMENTS REQUIRING FAIL-SAFE PERFORMANCE, SUCH AS IN THE OPERATION OF
# NUCLEAR FACILITIES, AIRCRAFT NAVIGATION OR COMMUNICATION SYSTEMS, AIR
# TRAFFIC CONTROL, DIRECT LIFE SUPPORT MACHINES, OR WEAPONS SYSTEMS.
# -----------------------------------------------------------------------------

from typing import NewType, Union

# --- 1. Phantom Types for Registers ---
# These allow the compiler to distinguish between a number '0' 
# and the register 'PINS' (which maps to address 0).
class PIORegister:
    def __init__(self, address: int):
        self.address = address

# --- 2. Register Definitions (Matching C++ resolve_operand) ---
# Your C++ Backend maps these specific addresses to strings:
PINS = PIORegister(0)  # Maps to "PINS"
PIN  = PIORegister(1)  # Maps to "PIN"
GPIO = PIORegister(2)  # Maps to "GPIO"
NULL = PIORegister(3)  # Maps to "NULL"
ISR  = PIORegister(4)  # Maps to "ISR"
OSR  = PIORegister(5)  # Maps to "OSR"

# --- 3. Configuration Constants ---
OUT = 0
IN  = 1
# Used for pull/push blocking
BLOCK   = 1
NOBLOCK = 0

# --- 4. Instructions (Intrinsics) ---
# Your Compiler Frontend must detect calls to these functions 
# and emit 'Call' nodes with the specific names expected by PIOCodeGen.

def pull(block: bool = True) -> None:
    """
    Pulls 32 bits from the TX FIFO into the OSR.
    Maps to C++: __pio_pull
    """
    # The compiler replaces this call with the intrinsic.
    # Python runtime just ignores it.
    pass

def push(block: bool = True) -> None:
    """
    Pushes 32 bits from the ISR into the RX FIFO.
    Maps to C++: __pio_push
    """
    pass

def out(destination: Union[PIORegister, int], bit_count: int) -> None:
    """
    Shifts 'bit_count' bits out of OSR to 'destination'.
    Usage: out(PINS, 1) or out(X, 32)
    Maps to C++: __pio_out
    """
    pass

def in_(source: Union[PIORegister, int], bit_count: int) -> None:
    """
    Shifts 'bit_count' bits from 'source' into ISR.
    Note: Named 'in_' because 'in' is a Python keyword.
    Usage: in_(PINS, 1)
    Maps to C++: __pio_in
    """
    pass

def wait(polarity: int, source: PIORegister, index: int) -> None:
    """
    Waits for a pin or IRQ.
    Usage: wait(1, PIN, 0)  -> Wait for pin mapping 0 to be High
           wait(0, GPIO, 15) -> Wait for raw GPIO 15 to be Low
    Maps to C++: __pio_wait
    """
    pass

def delay(cycles: int) -> None:
    """
    Adds delay cycles [n] to the *previous* instruction.
    Maps to C++: delay
    """
    pass

# --- 5. Decorator ---
def pio_program(func):
    """
    Marker to tell the compiler to treat this function 
    as a PIO State Machine, not CPU code.
    """
    return func
