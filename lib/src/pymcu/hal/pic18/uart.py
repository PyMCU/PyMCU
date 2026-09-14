from pymcu.types import uint8, uint16, int16, uint32, inline, const, compile_isr, Callable
from pymcu.exceptions import CompileError
from pymcu.hal.pic18.pic18_uart import *

from pymcu.hal.uart_text import (
    uart_write_str, uart_write_decimal_u8, uart_write_decimal_u16,
    uart_write_decimal_i16, uart_write_decimal_u32, uart_write_decimal_i32, uart_write_float,
)

class UART:
    def __init__(self, baud: const[uint16] = 9600, bits: const[uint8] = 8,
                 parity: const[uint8] = 0, stop: const[uint8] = 1):
        # bits, parity and stop describe the frame, and this HAL sends 8N1 and nothing else.
        # Accepting them and ignoring them is what the layers above used to do, so a program
        # that asked for 7E1 ran 8N1 with nothing said. Parity is 0 none, 1 even, 2 odd.
        if bits != 8 or parity != 0 or stop != 1:
            raise CompileError(
                "this UART sends 8 data bits, no parity and one stop bit, and this HAL does "
                "not program any other frame on this chip. Drop the bits, parity and stop "
                "arguments, or drive the frame you need over a UART on a part whose HAL "
                "programs it (the AVR one does).")
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
    def print_byte(self, value: uint8):
        pass

    @inline
    def print_uint16(self, value: uint16):
        pass

    @inline
    def available(self) -> uint8:
        return uart_read_ready()

    @inline
    def irq(self, handler: Callable):
        pass

    @inline
    def print_float(self, value: float):
        uart_write_float(value)
        self.write(10)
