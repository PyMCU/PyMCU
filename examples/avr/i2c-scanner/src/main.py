# ATmega328P: I2C bus scanner — probes addresses 0x01-0x7F
# Tests: I2C.ping() high-level probe, uint8 loop counter, hex printing
#
# Hardware: Arduino Uno
#   SDA ← PC4 (Arduino pin A4) — pulled up to VCC via 4.7 kΩ
#   SCL ← PC5 (Arduino pin A5) — pulled up to VCC via 4.7 kΩ
#   Serial terminal at 9600 baud — prints found device addresses
#
# Common I2C addresses:
#   0x3C / 0x3D  — SSD1306 OLED display
#   0x48-0x4F    — PCF8591 ADC / ADS1115 ADC
#   0x68 / 0x69  — MPU-6050 IMU / DS3231 RTC
#   0x76 / 0x77  — BMP280 / BME280 pressure sensor
#
from whipsnake.types import uint8
from whipsnake.hal.i2c import I2C
from whipsnake.hal.uart import UART


def main():
    uart = UART(9600)
    i2c  = I2C()

    uart.println("I2C SCANNER")
    uart.println("Scanning 0x01-0x7F...")

    found: uint8 = 0
    addr:  uint8 = 1

    while addr < 128:
        if i2c.ping(addr):
            uart.write_str("FOUND 0x")
            # Print high nibble
            hi: uint8 = (addr >> 4) & 0x0F
            if hi < 10:
                uart.write(hi + 48)    # '0'-'9'
            else:
                uart.write(hi + 55)    # 'A'-'F'
            # Print low nibble
            lo: uint8 = addr & 0x0F
            if lo < 10:
                uart.write(lo + 48)
            else:
                uart.write(lo + 55)
            uart.write('\n')
            found += 1

        addr += 1

    uart.write_str("Done. Found: ")
    uart.write(found + 48)
    uart.write('\n')

    while True:
        pass
