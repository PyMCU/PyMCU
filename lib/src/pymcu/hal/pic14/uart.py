from pymcu.types import uint8, uint16, int16, uint32, inline, const, compile_isr, Callable
from pymcu.exceptions import CompileError
from pymcu.hal.pic14.pic14_uart import *

from pymcu.hal.uart_text import (
    uart_write_str, uart_write_decimal_u8, uart_write_decimal_u16,
    uart_write_decimal_i16, uart_write_decimal_u32, uart_write_decimal_i32, uart_write_float,
)

class UART:
    def __init__(self, baud: const[uint16] = 9600):
        uart_init(baud)

    @inline
    def write(self, data: uint8):
        uart_write(data)

    @inline
    def read(self) -> uint8:
        return uart_read()

    @inline
    def write_str(self, s: const[str]):
        uart_write_str(s)

    @inline
    def println(self, s: const[str]):
        self.write_str(s)
        self.write(10)

    @inline
    def read_ready(self) -> uint8:
        return uart_read_ready()

    @inline
    def available(self) -> uint8:
        return uart_read_ready()

    @inline
    def print_byte(self, value: uint8):
        uart_write_decimal_u8(value)

    @inline
    def print_uint16(self, value: uint16):
        uart_write_decimal_u16(value)

    @inline
    def print_int16(self, value: int16):
        uart_write_decimal_i16(value)

    @inline
    def print_uint32(self, value: uint32):
        uart_write_decimal_u32(value)

    @inline
    def print_int32(self, value: int32):
        uart_write_decimal_i32(value)

    @inline
    def print_float(self, value: float):
        uart_write_float(value)

    @inline
    def irq(self, handler: Callable):
        raise CompileError("UART interrupts are not implemented for PIC14")
