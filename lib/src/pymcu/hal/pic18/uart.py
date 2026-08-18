from pymcu.types import uint8, uint16, int16, uint32, inline, const, compile_isr, Callable
from pymcu.hal.pic18.pic18_uart import *
from pymcu.hal.pic18.pic18_uart import uart_write_str

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
