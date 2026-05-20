# MicroPython utime module integration test fixture
#
# Verifies that utime.sleep_ms() introduces the expected delay.
#
# Expected UART output:
#   Byte 0: 0x41 ('A') -- sent immediately before sleep
#   Byte 1: 0x42 ('B') -- sent after utime.sleep_ms(500)
#
from machine import UART
from utime import sleep_ms


def main():
    uart = UART(0, 9600)
    uart.write(0x41)      # 'A' -- before sleep
    sleep_ms(500)
    uart.write(0x42)      # 'B' -- after 500 ms delay
    while True:
        pass
