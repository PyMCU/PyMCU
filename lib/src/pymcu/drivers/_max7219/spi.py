# MAX7219 SPI implementation (AVR)
# Loaded by max7219.py via: from pymcu.drivers._max7219.spi import max7219_write_reg
#
# One MAX7219 write = CS low, addr byte, data byte, CS high. The work lives in a REAL
# subroutine (primitives only, fixed SS pin) so the ~13 init/clear register writes share
# ONE copy instead of force-inlining a full SPI transaction per call. (A method that
# receives the ZCA SPI instance must be inlined; this takes only primitives.)
from pymcu.types import uint8
from pymcu.hal.avr.spi.avr import spi_select, spi_deselect, spi_transfer


def max7219_write_reg(reg: uint8, val: uint8):
    spi_select()
    spi_transfer(reg)
    spi_transfer(val)
    spi_deselect()
