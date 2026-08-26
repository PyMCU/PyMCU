# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

# `typing` is not resolvable by this compiler and never has been, so importing it here made
# pymcu.pio unimportable in every spelling -- an ImportError raised inside our own stdlib
# rather than in the reader's program (issue #199). It was used for two Union annotations,
# below, which are written as comments now: this module is read for the NAMES it binds, and
# nothing consumes the annotations. PIOCodeGenTests builds an equivalent module with no
# annotations at all and assembles the same program from it.

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
    """Pulls 32 bits from the TX FIFO into the OSR."""
    pass

def push(block: bool = True) -> None:
    """Pushes 32 bits from the ISR into the RX FIFO."""
    pass

def out(destination, bit_count: int) -> None:
    """Shifts bit_count bits out of OSR to destination. Usage: out(PINS, 1)

    `destination` is a PIORegister or a plain int address; it carries no annotation because
    either is accepted and PyMCU has no union spelling.
    """
    pass

def in_(source, bit_count: int) -> None:
    """Shifts bit_count bits from source into ISR. Named in_ because in is a keyword.

    `source` is a PIORegister or a plain int address; see out().
    """
    pass

def wait(polarity: int, source: PIORegister, index: int) -> None:
    """Waits for a pin or IRQ. Usage: wait(1, PIN, 0) or wait(0, GPIO, 15)"""
    pass

def delay(cycles: int) -> None:
    """Adds delay cycles to the previous instruction."""
    pass

# --- 5. Decorator ---
def pio_program(func):
    """
    Marker to tell the compiler to treat this function 
    as a PIO State Machine, not CPU code.
    """
    return func
