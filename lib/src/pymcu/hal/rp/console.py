# PyMCU RP-family console -- free-function UART value writers for f-string streaming.
# Shared by RP2040 and RP2350: both chips expose the same PL011 register names
# (UART0_DR / UART0_FR / UART_FR_TXFF); only the base address differs, and that
# lives in the per-chip register map.
# SPDX-License-Identifier: MIT
from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint16, int16, uint32, int32, const, inline

if __CHIP__.name == "rp2350":
    from pymcu.chips.rp2350 import UART0_DR, UART0_FR, UART_FR_TXFF
else:
    from pymcu.chips.rp2040 import UART0_DR, UART0_FR, UART_FR_TXFF


def uart_write(data: uint8):
    while (UART0_FR.value >> UART_FR_TXFF) & 1:
        pass
    UART0_DR.value = data


def uart_write_fmt(value: int32, base: uint8, width: uint8, flags: uint8):
    zero_pad: uint8 = flags & 0x04
    pad: uint8 = 32
    if zero_pad != 0:
        pad = 48
    neg: uint8 = 0
    mag: uint32 = 0
    if (flags & 0x02) and value < 0:
        neg = 1
        mag = uint32(0 - value)
    else:
        mag = uint32(value)
    if neg != 0 and zero_pad != 0:
        uart_write(45)
        if width > 0:
            width = width - 1
    buf: uint8[32] = [0] * 32
    n: uint8 = 0
    if mag == 0:
        buf[0] = 48
        n = 1
    else:
        while mag > 0:
            d: uint8 = 0
            if base == 16:
                d = uint8(mag & 0xF)
                mag = mag >> 4
            elif base == 10:
                d = uint8(mag % 10)
                mag = mag // 10
            elif base == 8:
                d = uint8(mag & 0x7)
                mag = mag >> 3
            else:
                d = uint8(mag & 1)
                mag = mag >> 1
            if d < 10:
                buf[n] = d + 48
            elif flags & 0x01:
                buf[n] = d - 10 + 65
            else:
                buf[n] = d - 10 + 97
            n = n + 1
    if neg != 0 and zero_pad == 0:
        buf[n] = 45
        n = n + 1
    while n < width and n < 32:
        buf[n] = pad
        n = n + 1
    while n > 0:
        n = n - 1
        uart_write(buf[n])


