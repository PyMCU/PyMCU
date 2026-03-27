# ATmega328P: CRC-8 Dallas/Maxim via FFI (avr-libc / Arduino OneWire)
#
# Calls crc8_update() from crc8.c, which wraps _crc_ibutton_update() from
# avr-libc <util/crc16.h>. This is the exact CRC used by the Arduino
# OneWire library to verify 1-Wire ROM codes and DS18B20 scratchpad data.
#
# Hardware: Arduino Uno -- UART TX on PD1, 9600 baud.
#
# Two test vectors:
#
#   1. ROM check: compute CRC-8 over a 7-byte DS18B20-style ROM
#      (family code 0x28 + 6 address bytes), then feed the resulting CRC
#      byte back. A valid 8-byte ROM always produces a final CRC of 0x00.
#      Output: "R:XX\n" (CRC of the 7 data bytes), "V:00\n" (self-check)
#
#   2. Known pair: CRC-8 of the single byte 0x00 from seed 0x00 is 0x00.
#      Output: "Z:00\n"
#
# To cross-check on Arduino:
#   #include <OneWire.h>
#   OneWire ow(2);
#   uint8_t rom[7] = {0x28,0x11,0x22,0x33,0x44,0x55,0x66};
#   Serial.println(OneWire::crc8(rom, 7), HEX);   // should match R:XX
#
from pymcu.types import uint8, inline
from pymcu.hal.uart import UART
from pymcu.ffi import extern


@extern("crc8_update")
def crc8_update(crc: uint8, data: uint8) -> uint8:
    pass


@inline
def print_hex(uart: UART, tag: uint8, val: uint8):
    uart.write(tag)
    uart.write(':')
    uart.write_hex(val)
    uart.write('\n')


def main():
    uart = UART(9600)
    uart.println("CRC8")

    # --- Test 1: DS18B20-style ROM self-check ---
    # Compute CRC over 7 ROM bytes (family 0x28 + 6 address bytes)
    crc: uint8 = 0
    crc = crc8_update(crc, 0x28)
    crc = crc8_update(crc, 0x11)
    crc = crc8_update(crc, 0x22)
    crc = crc8_update(crc, 0x33)
    crc = crc8_update(crc, 0x44)
    crc = crc8_update(crc, 0x55)
    crc = crc8_update(crc, 0x66)
    print_hex(uart, 'R', crc)   # CRC of the 7 ROM bytes

    # Self-check: CRC over all 8 bytes (7 data + CRC byte) must be 0x00.
    # This is exactly the check OneWire.cpp performs when reading a ROM code.
    chk: uint8 = 0
    chk = crc8_update(chk, 0x28)
    chk = crc8_update(chk, 0x11)
    chk = crc8_update(chk, 0x22)
    chk = crc8_update(chk, 0x33)
    chk = crc8_update(chk, 0x44)
    chk = crc8_update(chk, 0x55)
    chk = crc8_update(chk, 0x66)
    chk = crc8_update(chk, crc)
    print_hex(uart, 'V', chk)   # expect 0x00

    # --- Test 2: trivial zero check ---
    z: uint8 = crc8_update(0, 0)
    print_hex(uart, 'Z', z)     # expect 0x00

    uart.println("OK")

    while True:
        pass
