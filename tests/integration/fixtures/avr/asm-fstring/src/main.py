# PyMCU -- asm-fstring: asm() with f-string template and const[uint8] params.
#
# Tests that asm(f"INSTR {port}, {bit}") works when all interpolated
# expressions are compile-time constants (const[uint8] function parameters).
#
# Checkpoint 1: sbi(0x0A, 5) -> SBI DDRD, 5 -- sets PD5 as output
#   Verify DDRD[5] == 1, then BREAK
#
# Checkpoint 2: sbi(0x0B, 5) -> SBI PORTD, 5 -- drives PD5 high
#   Verify PORTD[5] == 1, then BREAK
#
# Checkpoint 3: cbi(0x0B, 5) -> CBI PORTD, 5 -- drives PD5 low
#   Verify PORTD[5] == 0, then BREAK
#
# SBI/CBI I/O addresses (ATmega328P, 5-bit field, 0x00-0x1F):
#   DDRD  I/O = 0x0A (10)
#   PORTD I/O = 0x0B (11)
#
from pymcu.types import uint8, inline, asm


@inline
def sbi(port: const[uint8], bit: const[uint8]):
    asm(f"SBI {port}, {bit}")


@inline
def cbi(port: const[uint8], bit: const[uint8]):
    asm(f"CBI {port}, {bit}")


def main():
    # Checkpoint 1: set PD5 as output via f-string SBI
    sbi(0x0A, 5)
    asm("BREAK")

    # Checkpoint 2: drive PD5 high via f-string SBI
    sbi(0x0B, 5)
    asm("BREAK")

    # Checkpoint 3: drive PD5 low via f-string CBI
    cbi(0x0B, 5)
    asm("BREAK")

    while True:
        pass
