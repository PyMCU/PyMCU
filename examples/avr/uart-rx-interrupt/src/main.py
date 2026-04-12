# ATmega328P: USART RX interrupt + 16-byte ring buffer echo
#
# Demonstrates:
#   - uart.irq(handler): registers handler at the USART_RX vector,
#     enables RXCIE0 and SEI automatically
#   - uart_rx_isr(): public ring-buffer filler imported from pymcu.hal.uart
#   - uart.rx_available() / uart.rx_read(): non-blocking read from main loop
#
# Hardware: Arduino Uno
#   UART TX/RX at 9600 baud
#
# Output:
#   "RXIRQ\n"      -- boot banner
#   Each received byte is echoed back via TX.
#
from pymcu.types import uint8
from pymcu.hal.uart import UART, uart_rx_isr


def on_rx():
    uart_rx_isr()    # store received byte in the 16-byte ring buffer


def main():
    uart = UART(9600)
    uart.irq(on_rx)  # registers on_rx at USART_RX (0x0024), enables RXCIE0 + SEI

    uart.println("RXIRQ")

    while True:
        if uart.rx_available():
            c: uint8 = uart.rx_read()
            uart.write(c)
