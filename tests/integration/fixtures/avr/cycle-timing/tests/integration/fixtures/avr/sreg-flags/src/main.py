# PyMCU -- sreg-flags: SREG flag state verification via BREAK checkpoints
#
# Uses non-inline helper functions so that both operands of ADD/SUB are
# runtime-variable from the callee's perspective. This prevents the compiler
# from constant-folding the arithmetic and forces real ADD/SUB machine
# instructions to be emitted, which set SREG flags correctly.
#
# Key property: STS/OUT and MOV instructions that follow the ADD/SUB inside
# the helper do NOT modify SREG, nor do CALL/RET. So the flag state at each
# BREAK directly reflects the last ADD or SUB executed inside the helper.
#
# Checkpoints:
#   1 -- add_u8(255, 1) = 0    : ADD sets C=1, Z=1; interrupts still disabled
#   2 -- add_u8(64,  64) = 128 : ADD sets N=1, V=1 (signed overflow), C=0, Z=0
#   3 -- sub_u8(10,  10) = 0   : SUB sets Z=1, C=0, N=0, V=0
#   4 -- asm("SEI")             : global interrupt enable flag I is now set
#
# Data-space address used to export the arithmetic result:
#   GPIOR0 = 0x3E
#
from pymcu.chips.atmega328p import GPIOR0
from pymcu.types import uint8, asm


def add_u8(a: uint8, b: uint8) -> uint8:
    """Return a+b. Both params are runtime: forces ADD Rd,Rr to be emitted."""
    return a + b


def sub_u8(a: uint8, b: uint8) -> uint8:
    """Return a-b. Both params are runtime: forces SUB Rd,Rr to be emitted."""
    return a - b


def main():

    # --- Checkpoint 1: 255 + 1 = 0 (C=1, Z=1, N=0, V=0) ---
    # ADD 0xFF + 0x01 = 0x100; low byte = 0x00 -> Z=1; carry out -> C=1
    c: uint8 = add_u8(255, 1)
    GPIOR0.value = c     # OUT/STS: does not touch SREG
    asm("BREAK")

    # --- Checkpoint 2: 64 + 64 = 128 (N=1, V=1, C=0, Z=0) ---
    # Result 0x80 has bit-7 set -> N=1
    # Signed: +64 + +64 = +128 overflows signed 8-bit range -> V=1
    # No carry out of bit 7 -> C=0
    r: uint8 = add_u8(64, 64)
    GPIOR0.value = r     # does not touch SREG
    asm("BREAK")

    # --- Checkpoint 3: 10 - 10 = 0 (Z=1, C=0, N=0, V=0) ---
    z: uint8 = sub_u8(10, 10)
    GPIOR0.value = z     # does not touch SREG
    asm("BREAK")

    # --- Checkpoint 4: global interrupts enabled via SEI ---
    asm("SEI")           # sets SREG I flag (bit 7)
    asm("BREAK")         # simulator stops here; I flag must be visible

    while True:
        pass
