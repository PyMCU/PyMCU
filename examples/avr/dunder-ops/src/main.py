# PyMCU -- dunder-ops: Dunder method operator overloading (F7)
#
# Demonstrates:
#   F7: __add__, __sub__, __mul__, __len__, __contains__, __getitem__, __setitem__
#
# Output on UART (9600 baud):
#   "DO\n"    -- boot banner
#   "A:07\n"  -- Vec(3,4) + Vec(1,2) -> x component = 3+1 = 4... wait: 3+1=4, let me recalc
#             -- Actually Vec(3,4).__add__(Vec(1,3)) -> x=3+1=4, y=4+3=7; we print y=7
#   "S:02\n"  -- Vec(5,4) - Vec(3,2) -> x=2; print x=2
#   "L:02\n"  -- len(Vec(3,4)) = 2
#   "C:01\n"  -- 3 in Vec(3,4) -> True = 1
#   "G:04\n"  -- Vec(3,4)[1] -> y component = 4
#
from pymcu.types import uint8, inline
from pymcu.hal.uart import UART


def nibble_hi(val: uint8) -> uint8:
    n: uint8 = (val >> 4) & 0x0F
    if n < 10:
        return n + 48
    return n + 55


def nibble_lo(val: uint8) -> uint8:
    n: uint8 = val & 0x0F
    if n < 10:
        return n + 48
    return n + 55


class Vec:
    x: uint8
    y: uint8

    @inline
    def __init__(self, xv: uint8, yv: uint8):
        self.x = xv
        self.y = yv

    @inline
    def __add__(self, other: uint8) -> uint8:
        return Vec(self.x + other.x, self.y + other.y)

    @inline
    def __sub__(self, other: uint8) -> uint8:
        return Vec(self.x - other.x, self.y - other.y)

    @inline
    def __len__(self) -> uint8:
        return 2

    @inline
    def __contains__(self, val: uint8) -> uint8:
        if val == self.x:
            return 1
        if val == self.y:
            return 1
        return 0

    @inline
    def __getitem__(self, idx: uint8) -> uint8:
        match idx:
            case 0:
                return self.x
            case _:
                return self.y


def main():
    uart = UART(9600)
    uart.println("DO")

    v1 = Vec(3, 4)
    v2 = Vec(1, 3)
    v3 = v1 + v2

    # v3.y = 4 + 3 = 7
    uart.write('A')
    uart.write(':')
    uart.write(nibble_hi(v3.y))
    uart.write(nibble_lo(v3.y))
    uart.write('\n')

    # subtraction: Vec(5,4) - Vec(3,2) -> x=2
    va = Vec(5, 4)
    vb = Vec(3, 2)
    vc = va - vb
    uart.write('S')
    uart.write(':')
    uart.write(nibble_hi(vc.x))
    uart.write(nibble_lo(vc.x))
    uart.write('\n')

    # __len__
    vl = Vec(3, 4)
    n: uint8 = len(vl)
    uart.write('L')
    uart.write(':')
    uart.write(nibble_hi(n))
    uart.write(nibble_lo(n))
    uart.write('\n')

    # __contains__: 3 in Vec(3,4) -> True = 1
    vc2 = Vec(3, 4)
    found: uint8 = 3 in vc2
    uart.write('C')
    uart.write(':')
    uart.write(nibble_hi(found))
    uart.write(nibble_lo(found))
    uart.write('\n')

    # __getitem__: Vec(3,4)[1] -> y = 4
    vg = Vec(3, 4)
    elem: uint8 = vg[1]
    uart.write('G')
    uart.write(':')
    uart.write(nibble_hi(elem))
    uart.write(nibble_lo(elem))
    uart.write('\n')

    while True:
        pass
