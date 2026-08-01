# MAX7219 SPI implementation (AVR)
# Loaded by max7219.py via: from pymcu.drivers._max7219.spi import max7219_write_reg
#
# One MAX7219 write = CS low, addr byte, data byte, CS high. The two transfers live in a
# REAL subroutine (primitives only) so the ~13 init/clear register writes share ONE copy
# instead of force-inlining a full SPI transaction per call. (A method that receives the
# ZCA SPI instance must be inlined; this takes only primitives.)
# CS is NOT toggled here: which pin it is lives in the SPI instance, which a primitives-only
# subroutine cannot see. MAX7219._write_reg brackets this call with spi.select()/deselect()
# so a bus configured with SPI(cs=...) drives the pin the user asked for.
from pymcu.types import uint8
from pymcu.hal.avr.spi.avr import spi_transfer


def max7219_write_reg(reg: uint8, val: uint8):
    spi_transfer(reg)
    spi_transfer(val)
