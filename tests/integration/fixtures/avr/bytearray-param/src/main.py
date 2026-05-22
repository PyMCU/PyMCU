# PyMCU -- bytearray-param: pass a local array as a bytearray pointer argument.
#
# Verifies that:
#   1. A local bytearray is passed by pointer (base address) to a function.
#   2. The callee can read bytes via buf[i] (pointer-indirect access).
#   3. The callee can write bytes via buf[i] = x (pointer-indirect store).
#
# Checkpoints (written to GPIOR registers, data-space):
#   GPIOR0 (0x3E) = buf[0] after fill_ascending() = 0x10
#   GPIOR1 (0x4A) = buf[2] after fill_ascending() = 0x12
#   GPIOR2 (0x4B) = sum_buf() = 0x10 + 0x11 + 0x12 = 0x33
#
from pymcu.types import uint8
from pymcu.chips.atmega328p import GPIOR0, GPIOR1, GPIOR2
from pymcu.types import asm


def fill_ascending(buf: bytearray, start: uint8):
    buf[0] = start
    buf[1] = start + 1
    buf[2] = start + 2


def sum_buf(buf: bytearray) -> uint8:
    result: uint8 = buf[0] + buf[1] + buf[2]
    return result


def main():
    data: uint8[3] = [0, 0, 0]
    fill_ascending(data, 0x10)
    GPIOR0.value = data[0]   # 0x10
    GPIOR1.value = data[2]   # 0x12
    GPIOR2.value = sum_buf(data)  # 0x33
    asm("BREAK")
    while True:
        pass
