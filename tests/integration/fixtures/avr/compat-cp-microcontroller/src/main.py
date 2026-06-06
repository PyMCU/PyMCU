# Clean version (no debug markers) -- reproduce the hang.
import board
import busio
import microcontroller
from microcontroller import WatchDogMode
from pymcu.types import uint8


def main():
    uart = busio.UART(board.TX, board.RX, baudrate=9600)
    out: uint8[1]

    microcontroller.nvm[0] = 0x5A
    out[0] = microcontroller.nvm[0]
    uart.write(out)                      # 0x5A

    microcontroller.watchdog.timeout = 2
    microcontroller.watchdog.mode = WatchDogMode.RESET
    microcontroller.watchdog.feed()
    microcontroller.watchdog.deinit()

    out[0] = microcontroller.cpu.reset_reason
    uart.write(out)                      # reset_reason

    uart.write(b"D")                     # done
    while True:
        pass
