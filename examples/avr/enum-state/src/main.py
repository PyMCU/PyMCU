from pymcu.hal.uart import UART

def main():
    uart = UART(9600)
    # Constant fold: uint8(300) = 44, uint8(256) = 0
    uart.write(uint8(300))
    uart.write(uint8(256))
    uart.write(uint16(42))
