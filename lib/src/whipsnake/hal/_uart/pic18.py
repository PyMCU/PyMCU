from whipsnake.types import uint8, uint16, inline, const, ptr

# Placeholder for PIC18 UART implementation

@inline
def uart_init(baud: const[uint16]):
    pass

@inline
def uart_write(data: uint8):
    pass

@inline
def uart_read() -> uint8:
    return 0
