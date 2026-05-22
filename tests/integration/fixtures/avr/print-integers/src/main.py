from pymcu.hal.uart import UART
from pymcu.types import uint16, int16, uint32

uart = UART(9600)

v16: uint16 = 1234
uart.print_uint16(v16)

neg: int16 = -500
uart.print_int16(neg)

big: uint32 = 123456
uart.print_uint32(big)

uart.println("done")
