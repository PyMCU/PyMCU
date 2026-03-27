# ATmega328P: BMP280 pressure and temperature sensor over I2C
#
# Demonstrates the BMP280 driver. Configures the sensor in normal mode
# and reads raw temperature and pressure ADC values.
# Hardware:
#   SDA -> PC4 (A4), SCL -> PC5 (A5), I2C address 0x76 (SDO=GND)
#
# UART output (9600 baud):
#   "BMP280\n"   -- boot banner
#   "OK\n"       -- init complete
#   "T:XXXX\n"   -- raw temperature MSB+LSB (hex high byte then low byte)
#   "P:XXXX\n"   -- raw pressure MSB+LSB
#
from pymcu.types import uint8, uint16
from pymcu.hal.uart import UART
from pymcu.hal.i2c import I2C
from pymcu.drivers.bmp280 import BMP280
from pymcu.time import delay_ms



def main():
    uart = UART(9600)
    i2c = I2C()
    bmp = BMP280(i2c, 0x76)

    uart.println("BMP280")

    bmp.init()

    uart.println("OK")

    # Wait for first measurement to complete in normal mode
    delay_ms(10)

    temp_raw: uint16 = bmp.read_temp_raw()
    press_raw: uint16 = bmp.read_press_raw()

    # Print raw temperature (high byte then low byte as hex)
    temp_hi: uint8 = (temp_raw >> 8) & 0xFF
    temp_lo: uint8 = temp_raw & 0xFF

    uart.write('T')
    uart.write(':')
    uart.write_hex(temp_hi)
    uart.write_hex(temp_lo)
    uart.write('\n')

    # Print raw pressure
    press_hi: uint8 = (press_raw >> 8) & 0xFF
    press_lo: uint8 = press_raw & 0xFF

    uart.write('P')
    uart.write(':')
    uart.write_hex(press_hi)
    uart.write_hex(press_lo)
    uart.write('\n')

    while True:
        pass
