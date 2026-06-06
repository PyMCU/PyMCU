# CircuitPython time module integration test fixture
#
# Verifies that time.sleep() introduces the expected delay.
#
# Expected UART output:
#   Byte 0: 0x41 ('A') -- sent immediately before sleep
#   Byte 1: 0x42 ('B') -- sent after time.sleep(0.5)
#
import board
import busio
import time


def main():
    uart = busio.UART(board.TX, board.RX, baudrate=9600)
    uart.write(b"A")      # 'A' -- before sleep
    time.sleep(0.5)       # 500 ms (float seconds, folded at compile time)
    uart.write(b"B")      # 'B' -- after 500 ms delay
    while True:
        pass
