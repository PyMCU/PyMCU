# ATmega328P: EEPROM read/write demo
# Tests: EEPROM.write(), EEPROM.read()
#
# Writes a known pattern to EEPROM addresses 0-3 and reads them back.
# Prints "EEPROM OK" if all values match, "EEPROM FAIL" otherwise.
#
from whisnake.types import uint8, uint16
from whisnake.hal.uart import UART
from whisnake.hal.eeprom import EEPROM

def main():
    uart = UART(9600)
    ee   = EEPROM()

    uart.println("EEPROM TEST")

    # Write pattern
    ee.write(0, 0xA1)
    ee.write(1, 0xB2)
    ee.write(2, 0xC3)
    ee.write(3, 0xD4)

    # Read back and verify
    a: uint8 = ee.read(0)
    b: uint8 = ee.read(1)
    c: uint8 = ee.read(2)
    d: uint8 = ee.read(3)

    if a == 0xA1 and b == 0xB2 and c == 0xC3 and d == 0xD4:
        uart.println("EEPROM OK")
    else:
        uart.println("EEPROM FAIL")

    while True:
        pass
