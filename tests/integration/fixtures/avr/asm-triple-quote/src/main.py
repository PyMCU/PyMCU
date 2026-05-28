# PyMCU -- asm-triple-quote: triple-quoted multiline asm() strings.
#
# Verifies that """...""" and '''...''' string literals are lexed correctly
# and that their content (including embedded newlines) is passed verbatim
# to the inline asm emitter.
#
# Checkpoint 1: triple-double-quoted asm sets GPIOR0 = 0x2A (42)
# Checkpoint 2: triple-single-quoted asm sets GPIOR0 = 0xFF
#
# Data-space address (ATmega328P): GPIOR0 = 0x3E
from pymcu.types import uint8, asm
from pymcu.chips.atmega328p import GPIOR0


def main():
    # Checkpoint 1: triple-double-quoted asm
    asm("""
        LDI r16, 42
        STS 0x3E, r16
    """)
    asm("BREAK")

    # Checkpoint 2: triple-single-quoted asm
    asm('''
        LDI r16, 0xFF
        STS 0x3E, r16
    ''')
    asm("BREAK")

    while True:
        pass
