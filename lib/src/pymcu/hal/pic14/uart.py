from pymcu.types import uint8, uint16, int16, uint32, inline, const, compile_isr, Callable
from pymcu.exceptions import CompileError
from pymcu.hal.pic14.pic14_uart import *
from pymcu.hal.pic14.pic14_uart import uart_write_str, uart_read_ready

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
        raise CompileError("UART.print_byte needs the decimal writers, which the PIC14 HAL does not have yet; use write() or write_str()")

    @inline
    def print_uint16(self, value: uint16):
        raise CompileError("UART.print_uint16 needs the decimal writers, which the PIC14 HAL does not have yet; use write() or write_str()")

    @inline
    def irq(self, handler: Callable):
        raise CompileError("UART interrupts are not implemented for PIC14")
