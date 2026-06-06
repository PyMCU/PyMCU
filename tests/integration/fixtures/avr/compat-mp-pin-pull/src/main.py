# machine.Pin pull-up and string pin-id integration test fixture
#
# Verifies two MicroPython machine.Pin API extensions:
#   1. Pin(int, mode, pull)  -- pull-up enables AVR internal pull-up resistor
#   2. Pin(str, mode)        -- direct port-string "PB5" accepted as pin_id
#
# Expected behaviour:
#   Boot: sends "READY\n", then 0x01 (pull-up holds PD2 high)
#   Loop: waits for UART byte; reads PD2 and sends its value

from machine import Pin, UART
from pymcu.types import uint8


def main():
    led  = Pin("PB5", Pin.OUT)
    btn  = Pin(2, Pin.IN, Pin.PULL_UP)
    uart = UART(0, 9600)

    uart.println("READY")

    # Initial read -- pull-up active, no external drive -> expect 0x01
    v: uint8 = btn.value()
    uart.write(v)

    # Loop: each received byte triggers a new read; send the pin state back.
    # Bug B1 was here: `v = btn.value()` (no annotation) folded to 255.
    # Fixed in Statements.cs (commit 0b07885): dead-code const return no longer
    # overwrites the live-path alias.
    while True:
        uart.read()
        v = btn.value()
        uart.write(v)
