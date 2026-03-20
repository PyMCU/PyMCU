# BMP280 I2C implementation
# Loaded by bmp280.py via: from pymcu.drivers._bmp280.i2c import ...
#
# BMP280 register map (relevant subset):
#   0xF3 = status     (measuring bit 3, im_update bit 0)
#   0xF4 = ctrl_meas  (osrs_t[7:5], osrs_p[4:2], mode[1:0])
#   0xF5 = config     (t_sb[7:5], filter[4:2], spi3w_en[0])
#   0xF7 = press_msb  (pressure raw [19:12])
#   0xF8 = press_lsb  (pressure raw [11:4])
#   0xF9 = press_xlsb (pressure raw [3:0] in bits [7:4])
#   0xFA = temp_msb   (temperature raw [19:12])
#   0xFB = temp_lsb   (temperature raw [11:4])
#   0xFC = temp_xlsb  (temperature raw [3:0] in bits [7:4])
#
# I2C read sequence: START + (addr<<1)+W + reg + RESTART + (addr<<1)+R + data + STOP
from pymcu.types import uint8, uint16, inline
from pymcu.hal.i2c import I2C


@inline
def bmp280_write_reg(i2c: uint8, addr: uint8, reg: uint8, val: uint8):
    # Write one byte to a BMP280 register.
    i2c.start()
    i2c.write((addr << 1) & 0xFE)
    i2c.write(reg)
    i2c.write(val)
    i2c.stop()


@inline
def bmp280_read_reg(i2c: uint8, addr: uint8, reg: uint8) -> uint8:
    # Read one byte from a BMP280 register.
    # Send register address then restart with read address.
    i2c.start()
    i2c.write((addr << 1) & 0xFE)
    i2c.write(reg)
    i2c.start()
    sla_r: uint8 = ((addr << 1) & 0xFE) | 1
    i2c.write(sla_r)
    result: uint8 = i2c.read_nack()
    i2c.stop()
    return result


@inline
def bmp280_init(i2c: uint8, addr: uint8):
    # Configure BMP280:
    #   ctrl_meas: osrs_t=001 (x1 temp), osrs_p=001 (x1 press), mode=11 (normal)
    #   0b 001 001 11 = 0x27
    bmp280_write_reg(i2c, addr, 0xF4, 0x27)
    # config: t_sb=000 (0.5ms standby), filter=000 (off), spi3w=0
    #   0b 000 000 0 0 = 0x00
    bmp280_write_reg(i2c, addr, 0xF5, 0x00)


@inline
def bmp280_read_temp_raw(i2c: uint8, addr: uint8) -> uint16:
    # Read 3 bytes from 0xFA (temp_msb, temp_lsb, temp_xlsb).
    # Return (MSB<<8)|LSB as uint16 (drops XLSB -- sufficient for display).
    msb: uint8 = bmp280_read_reg(i2c, addr, 0xFA)
    lsb: uint8 = bmp280_read_reg(i2c, addr, 0xFB)
    result: uint16 = msb
    result = (result << 8) | lsb
    return result


@inline
def bmp280_read_press_raw(i2c: uint8, addr: uint8) -> uint16:
    # Read 3 bytes from 0xF7 (press_msb, press_lsb, press_xlsb).
    # Return (MSB<<8)|LSB as uint16.
    msb: uint8 = bmp280_read_reg(i2c, addr, 0xF7)
    lsb: uint8 = bmp280_read_reg(i2c, addr, 0xF8)
    result: uint16 = msb
    result = (result << 8) | lsb
    return result
