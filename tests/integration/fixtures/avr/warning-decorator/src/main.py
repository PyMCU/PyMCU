# PyMCU -- warning-decorator: @warning is informational, not fatal
#
# A function decorated with @warning is reached and called. The compiler must
# print the note to stderr but STILL compile and run the function body (unlike
# the old @compile_message which aborted). Output on UART: "WARN\n" banner then
# "V:2A" (the @warning-decorated function returns 0x2A).
from pymcu.types import uint8, inline, warning
from pymcu.hal.uart import UART
from pymcu.time import delay_ms


def nibble_hex_hi(val: uint8) -> uint8:
    n: uint8 = (val >> 4) & 0x0F
    if n < 10:
        return n + 48
    return n + 55


def nibble_hex_lo(val: uint8) -> uint8:
    n: uint8 = val & 0x0F
    if n < 10:
        return n + 48
    return n + 55


@inline
@warning("compute() is flagged with @warning but must still compile and run")
def compute() -> uint8:
    return 0x2A


def main():
    uart = UART(9600)
    uart.println("WARN")

    v: uint8 = compute()
    uart.write('V')
    uart.write(':')
    uart.write(nibble_hex_hi(v))
    uart.write(nibble_hex_lo(v))
    uart.write('\n')

    while True:
        delay_ms(1000)
