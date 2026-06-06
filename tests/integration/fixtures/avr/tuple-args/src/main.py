# PyMCU -- tuple-args: tuple literal passed to an @inline function
#
# Verifies that a tuple literal argument binds to an inline parameter and is
# consumed both by constant subscript (color[0..2]) and by for-in unroll.
# Output on UART: "TUP\n" banner, then "RGB" (subscript) and "XY" (for-in).
from pymcu.types import uint8, inline
from pymcu.hal.uart import UART
from pymcu.time import delay_ms


@inline
def send_indexed(u: UART, color):
    # Constant subscript into a tuple-literal parameter.
    u.write(color[0])
    u.write(color[1])
    u.write(color[2])


@inline
def send_iter(u: UART, seq):
    # for-in unroll over a tuple-literal parameter.
    for c in seq:
        u.write(c)


def main():
    uart = UART(9600)
    uart.println("TUP")

    send_indexed(uart, (0x52, 0x47, 0x42))  # 'R' 'G' 'B'
    send_iter(uart, (0x58, 0x59))           # 'X' 'Y'
    uart.write('\n')

    while True:
        delay_ms(1000)
