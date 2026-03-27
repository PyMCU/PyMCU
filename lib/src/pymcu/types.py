# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

from typing import Generic, TypeVar
from typing import TypeAlias

T = TypeVar("T")


# noinspection PyPep8Naming
class ptr(Generic[T]):
    def __init__(self, address: int):
        self.address = address

    def __add__(self, other):
        return ptr(self.address + other)

    def __set__(self, instance, value):
        raise RuntimeError(
            "⚠️ Error: You're trying to write to a hardware register "
            "while running Python on your computer.\n"
            "This code must be compiled with 'pymcuc' and run on the microcontroller."
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

# noinspection PyPep8Naming
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
# intended bit width to the pymcuc compiler via the annotation text.
uint8:  TypeAlias = int
int8:   TypeAlias = int
uint16: TypeAlias = int
int16:  TypeAlias = int
uint32: TypeAlias = int
int32:  TypeAlias = int