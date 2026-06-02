# Sensor processing library.
#
# compute_crc8  -- CRC-8/MAXIM (poly 0x31) over one byte
# bit_reverse8  -- mirror the bit order of a byte: b7b6..b0 -> b0b1..b7
#
# Used by sensor_task (main.py) to process TCNT0 counter values.
from pymcu.types import uint8, asm


def compute_crc8(data: uint8) -> uint8:
    crc: uint8 = data
    i: int = 0
    while i < 8:
        if (crc & 0x80) != 0:
            crc = (crc << 1) ^ 0x31
        else:
            crc = crc << 1
        i = i + 1
    return crc


def bit_reverse8(v: uint8) -> uint8:
    lo: uint8 = 0
    asm("MOV %0, %1", lo, v)
    asm("SWAP %0", lo)
    hi: uint8 = 0
    asm("MOV %0, %1", hi, v)
    asm("LSR %0", hi)
    result: uint8 = lo ^ hi
    return result
