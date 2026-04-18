# PyMCU -- name-guard: if __name__ == "__main__": guard
#
# Exercises the compile-time __name__ constant.
# The if-guarded block runs because __name__ == "__main__" for the entry file.
#
# Checkpoints via GPIOR registers:
#   1 -- GPIOR0 = 0xAA: __name__ guard body executed
#   2 -- GPIOR1 = 0xBB: code after guard also executes
#
from pymcu.chips.atmega328p import GPIOR0, GPIOR1
from pymcu.types import asm


if __name__ == "__main__":
    GPIOR0.value = 0xAA
    asm("BREAK")

GPIOR1.value = 0xBB
asm("BREAK")

while True:
    pass
