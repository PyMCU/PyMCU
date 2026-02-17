from pymcu.chips import __CHIP__
from pymcu.types import uint8, inline

class Serial:
    @inline
    def __init__(self: uint8, baud: uint8):
        self.baud = baud
        if __CHIP__.name == "pic16f877a":
            from pymcu.hal._uart.pic16f877a import uart_init
            uart_init(baud)
        elif __CHIP__.name == "pic16f18877":
            from pymcu.hal._uart.pic16f18877 import uart_init
            uart_init(baud)
        elif __CHIP__.name == "pic18f45k50":
            from pymcu.hal._uart.pic18f45k50 import uart_init
            uart_init(baud)
        elif __CHIP__.name == "atmega328p":
            from pymcu.hal._uart.atmega328p import uart_init
            uart_init(baud)

    @inline
    def write(self: uint8, data: uint8):
        if __CHIP__.name == "pic16f877a":
            from pymcu.hal._uart.pic16f877a import uart_write
            uart_write(data)
        elif __CHIP__.name == "pic16f18877":
            from pymcu.hal._uart.pic16f18877 import uart_write
            uart_write(data)
        elif __CHIP__.name == "pic18f45k50":
            from pymcu.hal._uart.pic18f45k50 import uart_write
            uart_write(data)
        elif __CHIP__.name == "atmega328p":
            from pymcu.hal._uart.atmega328p import uart_write
            uart_write(data)

    @inline
    def write_byte(self: uint8, data: uint8):
        if __CHIP__.name == "pic16f877a":
            from pymcu.hal._uart.pic16f877a import uart_write_byte
            uart_write_byte(data)
        elif __CHIP__.name == "pic16f18877":
            from pymcu.hal._uart.pic16f18877 import uart_write_byte
            uart_write_byte(data)
        elif __CHIP__.name == "pic18f45k50":
            from pymcu.hal._uart.pic18f45k50 import uart_write_byte
            uart_write_byte(data)
        elif __CHIP__.name == "atmega328p":
            from pymcu.hal._uart.atmega328p import uart_write_byte
            uart_write_byte(data)
