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

from typing import Generic, TypeVar
from typing import TypeAlias

T = TypeVar("T")

class ptr(Generic[T]):
    def __init__(self, address: int):
        self.address = address

    def __add__(self, other):
        return ptr(self.address + other)

    def __set__(self, instance, value):
        raise RuntimeError(
            "⚠️ Error: You're trying to write to a hardware register "
            "while running Python on your computer.\n"
            "This code must be compiled with 'whipc' and run on the microcontroller."
        )

    def __getitem__(self, bit: int) -> bool:
        raise RuntimeError("Bit checking only works in compiled code")

    def __setitem__(self, bit: int, value: int):
        raise RuntimeError("Bit manipulation only works in compiled code")

    @property
    def value(self) -> T:
        raise RuntimeError("Reading from a register only works in compiled code")

    @value.setter
    def value(self, value: T):
        raise RuntimeError("Writing to a register only works in compiled code")

class const(Generic[T]):
    def __init__(self, value: object):
        self.value = value

    def __add__(self, other):
        return const(self.value + other)

    def __set__(self, instance, value):
        raise RuntimeError(
            "Cannot assign to a constant."
        )


def device_info(arch: str, chip: str = "", ram_size: int = 0):
    pass


def inline(f):
    return f


def asm(instruction: str):
    pass


def interrupt(f, vector: int = 0):
    if vector < 0:
        raise ValueError("Interrupt vector must be non-negative")
    return f


def compile_isr(handler, vector: int = 0):
    # Compiler intrinsic: marks `handler` as an ISR at `vector` without
    # requiring an @interrupt decorator on the function definition.
    # Called from Pin.irq() / pin_irq_setup() at compile time.
    pass


# Integer width aliases — defined as TypeAlias so int literals are always
# assignable (e.g. `x: uint16 = 0` is valid) while still communicating the
# intended bit width to the whipc compiler via the annotation text.
uint8:  TypeAlias = int
int8:   TypeAlias = int
uint16: TypeAlias = int
int16:  TypeAlias = int
uint32: TypeAlias = int
int32:  TypeAlias = int