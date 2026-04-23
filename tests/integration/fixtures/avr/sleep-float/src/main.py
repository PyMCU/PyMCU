# ATmega328P: sleep() with compile-time float literal folding.
#
# sleep(0.5) must compile to delay_ms(500) -- no runtime float math.
# Timing sentinels via UART allow the integration test to verify
# that each delay matches the expected duration.
#
#   'R' -- firmware ready (sent immediately)
#   'A' -- after sleep(0.5)  ~500ms
#   'B' -- after sleep(1.5)  another ~1500ms
#   'C' -- after sleep(1.0)  another ~1000ms

from pymcu.hal.uart import UART
from pymcu.time import sleep

def main():
    uart = UART(9600)
    uart.write('R')

    sleep(0.5)
    uart.write('A')

    sleep(1.5)
    uart.write('B')

    sleep(1.0)
    uart.write('C')

    while True:
        uart.write('H')
        sleep(0.5)
