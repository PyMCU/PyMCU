from whipsnake.types import uint8, uint16, inline, const, ptr

# Placeholder for PIC14 UART implementation
# Requires chip-specific register definitions (TXSTA, RCSTA, SPBRG, TXREG, RCREG)
# which vary by chip. For now, this is a stub.

@inline
def uart_init(baud: const[uint16]):
    pass

@inline
def uart_write(data: uint8):
    pass

@inline
def uart_read() -> uint8:
    return 0
