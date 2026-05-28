# PyMCU -- callable-array: Callable[N] array of function pointers.
#
# Verifies that a Callable[N] array can be declared with function-name
# initializers and dispatched with both constant and variable indices.
#
# Checkpoint 1: _tasks[0]() (constant index) -> GPIOR0 = 0xAA
# Checkpoint 2: _tasks[1]() (constant index) -> GPIOR0 = 0xBB
# Checkpoint 3: _tasks[idx]() with idx=0 (variable index) -> GPIOR0 = 0xAA
# Checkpoint 4: _tasks[idx]() with idx=1 (variable index) -> GPIOR0 = 0xBB
#
# Data-space address (ATmega328P): GPIOR0 = 0x3E
from pymcu.types import uint8, Callable, asm


def set_aa():
    asm("LDI r16, 0xAA")
    asm("STS 0x3E, r16")


def set_bb():
    asm("LDI r16, 0xBB")
    asm("STS 0x3E, r16")


_tasks: Callable[2] = [set_aa, set_bb]


def main():
    # Checkpoint 1: constant index 0
    _tasks[0]()
    asm("BREAK")

    # Checkpoint 2: constant index 1
    _tasks[1]()
    asm("BREAK")

    idx: uint8 = 0

    # Checkpoint 3: variable index 0
    _tasks[idx]()
    asm("BREAK")

    # Checkpoint 4: variable index 1
    idx = 1
    _tasks[idx]()
    asm("BREAK")

    while True:
        pass
