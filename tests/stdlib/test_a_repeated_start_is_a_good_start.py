"""Every TWI transaction takes a repeated START as a good START.

A write with stop=False leaves the bus held, and the next START the hardware sends on a
held bus is a repeated START: TWSR reads 0x10, not 0x08. `i2c_write_byte` already took
both, but `i2c_read_n` (and the other composite transactions) compared against 0x08 alone.
So MicroPython's write-then-read idiom, `i2c.writeto(addr, reg, False)` followed by
`i2c.readfrom_into(addr, buf)`, raised OSError EIO with the device present and answering.

Pinned as a source sweep over the AVR TWI HAL: any comparison of a status with the START
code 0x08 has to accept 0x10 in the same condition. The bus-level behaviour (RSTART on
the wire, then the read) is measured in firmware by the pymcu-avr fixture
mp-i2c-repeated-start.
"""

import re
from pathlib import Path

TWI = (Path(__file__).resolve().parents[2] / "lib" / "src" / "pymcu" / "hal" / "avr"
       / "i2c" / "avr.py")

_START_TEST = re.compile(r"^\s*if .*(?:==|!=)\s*0x08\b.*$", re.M)


def test_every_start_check_accepts_a_repeated_start():
    source = TWI.read_text()
    checks = _START_TEST.findall(source)
    assert checks, "the sweep found no START checks; the HAL moved and this test is blind"
    narrow = [line.strip() for line in checks if "0x10" not in line]
    assert not narrow, (
        "a START check that refuses a repeated START (0x10) fails every transaction "
        "that follows a stop=False write:\n  " + "\n  ".join(narrow))
