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


@inline
def uart_write_str(s: const[str]):
    for ch in s:
        uart_write(ch)


def uart_write_decimal_u32(value: uint32):
    if value == 0:
        uart_write(48)
        return
    buf: uint8[10] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0]
    n: uint8 = 0
    while value > 0:
        buf[n] = uint8(value % 10) + 48
        value = value // 10
        n = n + 1
    while n > 0:
        n = n - 1
        uart_write(buf[n])


def uart_write_decimal_u16(value: uint16):
    uart_write_decimal_u32(uint32(value))


def uart_write_decimal_u8(value: uint8):
    uart_write_decimal_u32(uint32(value))


def uart_write_decimal_i32(value: int32):
    if value < 0:
        uart_write(45)
        uart_write_decimal_u32(uint32(0 - value))
    else:
        uart_write_decimal_u32(uint32(value))


def uart_write_decimal_i16(value: int16):
    uart_write_decimal_i32(int32(value))


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


def uart_write_float(value: float):
    # Print a float with one decimal place (e.g. 23.5, -5.0) over UART0.
    # Same one-decimal contract as the AVR HAL's uart_write_float, so print(float)
    # behaves identically across architectures. Float math lowers to the bootrom
    # fast-float library on RP2040 and to the M33 FPU on RP2350; the integer
    # digits use the SIO divider (M0+) or native UDIV (M33).
    if value < 0.0:
        uart_write(45)
        value = 0.0 - value
    tenths: uint16 = uint16(value * 10.0)
    int_part: uint16 = tenths // 10
    frac: uint16 = tenths % 10
    if int_part >= 100:
        hundreds: uint16 = int_part // 100
        uart_write(48 + uint8(hundreds))
        tens: uint16 = (int_part // 10) % 10
        uart_write(48 + uint8(tens))
        units: uint16 = int_part % 10
        uart_write(48 + uint8(units))
    elif int_part >= 10:
        tens2: uint16 = int_part // 10
        uart_write(48 + uint8(tens2))
        units2: uint16 = int_part % 10
        uart_write(48 + uint8(units2))
    else:
        uart_write(48 + uint8(int_part))
    uart_write(46)
    uart_write(48 + uint8(frac))
