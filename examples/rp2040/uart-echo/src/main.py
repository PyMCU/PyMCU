# Echo bytes received on UART0 (GP0=TX, GP1=RX) back to the sender.
from pymcu.hal.uart import UART


def main():
    uart = UART(115200)
    uart.println("ECHO")
    while True:
        c = uart.read()
        uart.write(c)
