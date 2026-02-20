from pymcu.chips.atmega328p import UBRR0H, UBRR0L, UCSR0A, UCSR0B, UCSR0C, UDR0
from pymcu.types import uint8, uint16, inline, const

@inline
def uart_init(baud: const[uint16]):
    # Calculate UBRR value for 16MHz clock
    # UBRR = (F_CPU / (16 * baud)) - 1
    # Assuming F_CPU = 16000000
    ubrr: uint16 = (16000000 // (16 * baud)) - 1
    
    UBRR0H[0] = (ubrr >> 8) & 0xFF
    UBRR0L[0] = ubrr & 0xFF
    
    # Enable RX and TX
    UCSR0B[0] = (1 << 4) | (1 << 3) # RXEN0 | TXEN0
    
    # Frame format: 8 data, 1 stop bit (default)
    UCSR0C[0] = (1 << 1) | (1 << 2) # UCSZ01 | UCSZ00

@inline
def uart_write(data: uint8):
    # Wait for empty transmit buffer
    while (UCSR0A[0] & (1 << 5)) == 0: # UDRE0
        pass
    UDR0[0] = data

@inline
def uart_read() -> uint8:
    # Wait for data to be received
    while (UCSR0A[0] & (1 << 7)) == 0: # RXC0
        pass
    return UDR0[0]
