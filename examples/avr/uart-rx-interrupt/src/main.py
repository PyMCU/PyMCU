# ATmega328P: USART RX interrupt + 16-byte ring buffer echo
#
# Demonstrates:
#   - UART.enable_rx_interrupt(): sets RXCIE0 (bit 7 of UCSR0B)
#   - @interrupt(0x0024): USART_RX vector (byte 0x0024, word 0x0012)
#   - uart_rx_isr() HAL helper: reads UDR0 into ring buffer from ISR
#   - UART.rx_available() / UART.rx_read(): non-blocking read from main loop
#
# Hardware: Arduino Uno
#   UART TX/RX at 9600 baud
#
# Output:
#   "RXIRQ\n"      -- boot banner
#   Each received byte is echoed back via TX.
#
from whipsnake.types import uint8, interrupt, asm
from whipsnake.hal.uart import UART
from whipsnake.hal._uart.avr import uart_rx_isr


@interrupt(0x0024)
def usart_rx_isr():
    # USART_RX vector: fires when a byte arrives (RXC0=1).
    # uart_rx_isr() reads UDR0 and stores it in the ring buffer.
    uart_rx_isr()


def main():
    uart = UART(9600)
    uart.enable_rx_interrupt()

    asm("SEI")

    uart.println("RXIRQ")

    while True:
        if uart.rx_available():
            c: uint8 = uart.rx_read()
            uart.write(c)
