# PyMCU -- asm-constraints: inline ASM with %N register constraint placeholders.
#
# Tests: asm("instruction %0, ...", operand) syntax.
#
# The compiler loads each operand into a scratch register (R16, R17, ...),
# substitutes %N with the register name in the template, emits the assembly,
# then stores the scratch register value back into the variable.
#
# Checkpoint 1: asm("LDI %0, 42", result) -- output-only, sets result = 42
#   GPIOR0 = 0x2A (42)
#
# Checkpoint 2: asm("MOV %0, %1", dst, src) where src = 0xFF
#   GPIOR0 = 0xFF
#
# Checkpoint 3: asm("INC %0", val) where val = 9 -> 10
#   GPIOR0 = 0x0A (10)
#
# Data-space address (ATmega328P): GPIOR0 = 0x3E
#
from pymcu.types import uint8, asm
from pymcu.chips.atmega328p import GPIOR0


def main():
    # --- Checkpoint 1: LDI %0, 42 (load constant 42 into result) ---
    result: uint8 = 0
    asm("LDI %0, 42", result)
    GPIOR0.value = result
    asm("BREAK")

    # --- Checkpoint 2: MOV %0, %1 (copy src=255 to dst) ---
    src: uint8 = 0xFF
    dst: uint8 = 0
    asm("MOV %0, %1", dst, src)
    GPIOR0.value = dst
    asm("BREAK")

    # --- Checkpoint 3: INC %0 (increment val from 9 to 10) ---
    val: uint8 = 9
    asm("INC %0", val)
    GPIOR0.value = val
    asm("BREAK")

    while True:
        pass
