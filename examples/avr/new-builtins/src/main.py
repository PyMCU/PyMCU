# PyMCU -- new-builtins: new language feature tests
#
# Tests:
#   - zip(a, b) compile-time paired iteration
#   - reversed([list]) compile-time reversed iteration
#   - str(n) compile-time integer to decimal string
#   - pow(x, n) compile-time integer power
#   - x ** n compile-time power operator
#
# UART output (9600 baud):
#   "NB\n"     -- boot banner
#   "Z:XX\n"   -- zip sum: zip([1,2,3],[10,20,30]) => (1+10)+(2+20)+(3+30)=66=0x42
#   "R:XX\n"   -- reversed sum: reversed([5,10,15,20]) => 20+15+10+5=50=0x32
#   "S:42\n"   -- str(42) output via write_str
#   "P:XX\n"   -- pow(2,8) = 256, low byte = 0x00 -- use pow(3,4)=81=0x51 instead
#   "W:XX\n"   -- 2**10 = 1024, low byte = 0x00 -- use 2**6=64=0x40 instead
#
from pymcu.types import uint8, uint16
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


def main():
    uart = UART(9600)

    # Boot banner
    uart.write(78)   # N
    uart.write(66)   # B
    uart.write(10)   # \n

    # -- zip([1,2,3], [10,20,30]) sum of pairs => (1+10)+(2+20)+(3+30)=66=0x42 --
    total: uint8 = 0
    for x, y in zip([1, 2, 3], [10, 20, 30]):
        total = total + x + y
    uart.write(90)   # Z
    uart.write(58)   # :
    uart.write(nibble_hi(total))
    uart.write(nibble_lo(total))
    uart.write(10)   # \n

    # -- reversed([5,10,15,20]) => accumulate in reverse => 20+15+10+5=50=0x32 --
    rev_sum: uint8 = 0
    for v in reversed([5, 10, 15, 20]):
        rev_sum = rev_sum + v
    uart.write(82)   # R
    uart.write(58)   # :
    uart.write(nibble_hi(rev_sum))
    uart.write(nibble_lo(rev_sum))
    uart.write(10)   # \n

    # -- str(42) => "42" -- print it
    uart.write(83)   # S
    uart.write(58)   # :
    uart.write_str(str(42))
    uart.write(10)   # \n

    # -- pow(3, 4) = 81 = 0x51 --
    p: uint8 = pow(3, 4)
    uart.write(80)   # P
    uart.write(58)   # :
    uart.write(nibble_hi(p))
    uart.write(nibble_lo(p))
    uart.write(10)   # \n

    # -- 2 ** 6 = 64 = 0x40 --
    w: uint8 = 2 ** 6
    uart.write(87)   # W
    uart.write(58)   # :
    uart.write(nibble_hi(w))
    uart.write(nibble_lo(w))
    uart.write(10)   # \n

    # -- uart.read_nb() with no data pending => 0 --
    nb: uint8 = uart.read_nb()
    uart.write(78)   # N (reuse N, context is "NR" prefix below)
    uart.write(82)   # R
    uart.write(58)   # :
    uart.write(nibble_hi(nb))
    uart.write(nibble_lo(nb))
    uart.write(10)   # \n

    while True:
        pass
