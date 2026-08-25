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
    '''A typed compile-time memory address (memory-mapped I/O register).

    Declare a register once, with its width, and every access compiles to a
    direct load/store of that address -- no object exists at runtime:

        PORTB: ptr[uint8] = ptr(0x25)

        PORTB.value = 0xFF     # write the whole register (OUT / STS)
        x: uint8 = PORTB.value # read the whole register  (IN / LDS)
        PORTB[5] = 1           # set one bit (SBI in the I/O range)
        if PORTB[5]:           # test one bit (SBIS / SBIC)
            pass

    Every .value and [bit] access is VOLATILE in the compiled output: reads
    are never cached, writes are never elided or reordered by the optimizer
    (on ARM they lower to volatile LLVM loads/stores).

    The two access forms above are the only ones. A bare assignment such as
    `PORTB = 0xFF` would rebind the Python name instead of writing the
    register, so the compiler rejects it with an error naming both forms.

    The address expression may use constant arithmetic (`ptr(BASE + 0x40)`)
    or runtime operands (`ptr(BASE + 4 * n).value`); pointer advance (p + 1
    on an existing ptr), pointer difference and element indexing are not
    supported -- see the language limitations page.

    In plain CPython (running the file on your computer) every access raises
    RuntimeError: registers only exist on the microcontroller.
    '''

    def __init__(self, address: int):
        self.address = address

    def __add__(self, other: int) -> "ptr[T]":
        # Address arithmetic used while DECLARING a register (ptr(BASE) + off
        # is folded at compile time). Not runtime pointer advance.
        return ptr(self.address + other)

    def __set__(self, instance, value):
        raise RuntimeError(
            "Error: You're trying to write to a hardware register "
            "while running Python on your computer.\n"
            "This code must be compiled with 'pymcuc' and run on the microcontroller."
        )

    def __getitem__(self, bit: int) -> bool:
        '''Read one bit of the register (compiles to SBIS/SBIC or LDS+mask).'''
        raise RuntimeError("Bit checking only works in compiled code")

    def __setitem__(self, bit: int, value: int):
        '''Write one bit of the register (compiles to SBI/CBI or a masked store).'''
        raise RuntimeError("Bit manipulation only works in compiled code")

    @property
    def value(self) -> T:
        '''The whole register, read or written atomically at its declared width.

        Reading and writing .value is the canonical full-register access:

            UDR0.value = byte          # store
            byte = UDR0.value          # load
            TCCR1B.value = TCCR1B.value | 0x08   # read-modify-write
        '''
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


def device_info(arch: str, chip: str = "", ram_size: int = 0, flash_size: int = 0,
                eeprom_size: int = 0):
    # flash_size is program storage in BYTES as the programmer and the hex file
    # address it: native bytes on AVR and PIC18, words x2 on PIC12 and PIC14,
    # whose hex format is byte-doubled. Left at 0 where it is not a property of
    # the chip -- the RP parts execute from a board's external flash through the
    # XIP window -- and 0 there means "does not apply", never "tiny".
    #
    # These three sizes are the chip's memory geometry, and this call is the only
    # place they are declared. The compiler carries them to the backend in the
    # .mir, where the AVR backend reads flash_size to choose LPM or ELPM and
    # ram_size to place RAMEND. Omitting one is "not declared", not zero: a
    # backend that needs a size it was not given stops the build and names this
    # chip and this field. Each argument must be an integer literal or a
    # module-level integer constant of the same file.
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