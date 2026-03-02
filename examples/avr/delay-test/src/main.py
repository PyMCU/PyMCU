# ATmega328P: delay_ms timing test
# Demonstrates correct 16-bit software delays (values > 255).
#
# Sends a sentinel byte via UART before and after each delay so
# an integration test can verify the elapsed simulated time.
#
#   'R' (0x52) -- firmware ready (sent immediately)
#   'A' (0x41) -- after delay_ms(1000)   ~1 second
#   'B' (0x42) -- after delay_ms(3000)   ~3 seconds (16-bit: 3000 > 255)
#
from pymcu.hal.uart import UART
from pymcu.time import delay_ms

def main():
    uart = UART(9600)
    uart.write(82)    # 'R' -- ready

    delay_ms(1000)
    uart.write(65)    # 'A' -- 1000 ms elapsed

    delay_ms(3000)
    uart.write(66)    # 'B' -- another 3000 ms elapsed (3000 > 255 tests uint16)

    while True:
        uart.write(72)    # 'H' -- heartbeat so simulator does not stall
        delay_ms(500)
