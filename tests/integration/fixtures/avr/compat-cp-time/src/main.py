# CircuitPython time module integration test fixture
#
# Verifies that time.sleep_ms() introduces the expected delay.
#
# Expected UART output:
#   Byte 0: 0x41 ('A') -- sent immediately before sleep
#   Byte 1: 0x42 ('B') -- sent after time.sleep_ms(500)
#
import board
import busio
import time


def main():
    uart = busio.UART(board.TX, board.RX, baudrate=9600)
    uart.write(0x41)      # 'A' -- before sleep
    time.sleep_ms(500)
    uart.write(0x42)      # 'B' -- after 500 ms delay
    while True:
        pass
