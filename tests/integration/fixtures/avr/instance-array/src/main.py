# PyMCU -- instance-array: per-instance SRAM array member (framebuffer pattern)
#
# A ZCA class declares `self._data: uint8[n*2]` in __init__ (size folded from
# the constructor arg) and reads/writes it by RUNTIME index from its methods.
# This is the mechanism behind a NeoPixel framebuffer (pixels[i] = color).
# Output on UART: "IA\n" banner, then "ABCDEFGH" (8 bytes written then read back).
from pymcu.types import uint8, inline
from pymcu.hal.uart import UART
from pymcu.time import delay_ms


class Buffer:
    @inline
    def __init__(self, n: uint8):
        self._n = n
        self._data: uint8[n*2]   # 2*n bytes, zero-initialised

    @inline
    def set(self, i: uint8, v: uint8):
        self._data[i] = v

    @inline
    def get(self, i: uint8) -> uint8:
        return self._data[i]


class Strip:
    # Mimics the NeoPixel framebuffer: __setitem__ takes an (a, b) tuple and
    # writes it into a per-instance SRAM buffer at a runtime offset.
    @inline
    def __init__(self, n: uint8):
        self._n = n
        self._buf: uint8[n*2]

    @inline
    def __setitem__(self, index, color):
        self._buf[index * 2 + 0] = color[0]
        self._buf[index * 2 + 1] = color[1]

    @inline
    def raw(self, i: uint8) -> uint8:
        return self._buf[i]


def main():
    uart = UART(9600)
    uart.println("IA")

    buf = Buffer(4)            # 8-byte framebuffer

    i: uint8 = 0
    while i < 8:
        buf.set(i, 65 + i)    # 'A'..'H'
        i = i + 1

    j: uint8 = 0
    while j < 8:
        uart.write(buf.get(j))
        j = j + 1
    uart.write('\n')

    # __setitem__ with a tuple, writing into a per-instance framebuffer.
    strip = Strip(3)          # 3 "pixels", 2 bytes each = 6-byte buffer
    strip[0] = (0x50, 0x51)   # 'P' 'Q'
    strip[1] = (0x52, 0x53)   # 'R' 'S'
    strip[2] = (0x54, 0x55)   # 'T' 'U'

    k: uint8 = 0
    while k < 6:
        uart.write(strip.raw(k))
        k = k + 1
    uart.write('\n')

    while True:
        delay_ms(1000)
