# BMP280 I2C implementation (AVR)
# Loaded by bmp280.py via: from pymcu.drivers._bmp280.i2c import ...
#
# BMP280 register map (relevant subset):
#   0xF3 = status     0xF4 = ctrl_meas  0xF5 = config
#   0xF7..0xF9 = press_msb/lsb/xlsb     0xFA..0xFC = temp_msb/lsb/xlsb
#
# The per-register transaction lives in REAL subroutines (_bmp280_write / _bmp280_read)
# that take only primitives and drive the TWI registers directly. The public @inline
# wrappers keep the (i2c, addr, ...) signature for the ZCA call sites but route to them,
# so the init writes + raw-read sites share ONE copy each instead of force-inlining a full
# I2C transaction per call. (A function that receives the ZCA I2C instance must be inlined.)
from pymcu.types import uint8, uint16, inline
from pymcu.hal.avr.i2c.avr import i2c_start, i2c_write, i2c_stop, i2c_read_nack


def _bmp280_write(addr: uint8, reg: uint8, val: uint8):
    i2c_start()
    i2c_write((addr << 1) & 0xFE)
    i2c_write(reg)
    i2c_write(val)
    i2c_stop()


def _bmp280_read(addr: uint8, reg: uint8) -> uint8:
    i2c_start()
    i2c_write((addr << 1) & 0xFE)
    i2c_write(reg)
    i2c_start()
    i2c_write(((addr << 1) & 0xFE) | 1)
    result: uint8 = i2c_read_nack()
    i2c_stop()
    return result


@inline
def bmp280_write_reg(i2c: uint8, addr: uint8, reg: uint8, val: uint8):
    _bmp280_write(addr, reg, val)


@inline
def bmp280_read_reg(i2c: uint8, addr: uint8, reg: uint8) -> uint8:
    return _bmp280_read(addr, reg)


@inline
def bmp280_init(i2c: uint8, addr: uint8):
    # ctrl_meas: osrs_t=x1, osrs_p=x1, mode=normal -> 0x27 ; config: standby/filter off -> 0x00
    _bmp280_write(addr, 0xF4, 0x27)
    _bmp280_write(addr, 0xF5, 0x00)


@inline
def bmp280_read_temp_raw(i2c: uint8, addr: uint8) -> uint16:
    # (MSB<<8)|LSB from 0xFA/0xFB (drops XLSB -- sufficient for display).
    msb: uint8 = _bmp280_read(addr, 0xFA)
    lsb: uint8 = _bmp280_read(addr, 0xFB)
    result: uint16 = msb
    result = (result << 8) | lsb
    return result


@inline
def bmp280_read_press_raw(i2c: uint8, addr: uint8) -> uint16:
    # (MSB<<8)|LSB from 0xF7/0xF8.
    msb: uint8 = _bmp280_read(addr, 0xF7)
    lsb: uint8 = _bmp280_read(addr, 0xF8)
    result: uint16 = msb
    result = (result << 8) | lsb
    return result
