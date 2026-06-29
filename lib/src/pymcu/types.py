# -----------------------------------------------------------------------------
# PyMCU Standard Library & HAL Definitions
# Copyright (C) 2026 Ivan Montiel Cardona and the PyMCU Project Authors
#
# SPDX-License-Identifier: MIT
# Licensed under the MIT License. See LICENSE for details.
# -----------------------------------------------------------------------------

from typing import Callable, Generic, TypeVar
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
            "Error: You're trying to write to a hardware register "
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


def naked(f):
    return f


def used(f):
    # Keep this function alive with external linkage even when no Python code calls
    # it, so it can be invoked from inline asm (e.g. an RTOS `asm("bl scheduler")`).
    # Zero-cost marker -- the compiler anchors it in @llvm.used.
    return f


# Alias for those who want C-interop semantics explicitly.
export_c = used


def outline(f):
    # RFC 0001 Model A: marks a ZCA method to be compiled once as a shared
    # subroutine (instance fields passed as runtime params) instead of being
    # inlined per call site. Zero-cost marker -- the compiler does the work.
    return f


def warning(message: str):
    # Parametrised diagnostic decorator: when the pymcuc compiler expands a
    # call to the decorated function it prints `message` (once per function)
    # as an informational build-time note.  It does NOT abort compilation.
    # Use it to flag functions that pull in the heavier software-float
    # runtime, have reduced behaviour on bare metal, or otherwise warrant a
    # heads-up (e.g. AnalogOut on a chip without a DAC, or read() that cannot
    # allocate a bytes object).  At Python simulation time it is inert.
    def _wrap(f):
        return f
    return _wrap


def asm(instruction: str):
    pass


def interrupt(f, vector: int = 0):
    if vector < 0:
        raise ValueError("Interrupt vector must be non-negative")
    return f


def compile_isr(handler: Callable, vector: int = 0):
    # Compiler intrinsic: marks `handler` as an ISR at `vector` without
    # requiring an @interrupt decorator on the function definition.
    # Called from Pin.irq() / timer.irq() / spi.irq() / i2c.irq() at compile time.
    pass


def funcref(fn: Callable) -> int:
    # Compiler intrinsic: returns the word address of `fn` as a runtime
    # function pointer (uint16 on AVR).  The result must be stored in a
    # variable annotated with Callable and can be invoked via ICALL.
    # At Python simulation time this returns 0 (no-op).
    return 0


# Integer width aliases -- defined as TypeAlias so int literals are always
# assignable (e.g. `x: uint16 = 0` is valid) while still communicating the
# intended bit width to the pymcuc compiler via the annotation text.
uint8:  TypeAlias = int
int8:   TypeAlias = int
uint16: TypeAlias = int
int16:  TypeAlias = int
uint32: TypeAlias = int
int32:  TypeAlias = int