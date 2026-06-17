# HD44780 LCD driver (4-bit parallel interface)
# Zero-cost abstraction -- follows the DHT11/NeoPixel driver pattern.
#
# Usage:
#   from pymcu.drivers.lcd import LCD
#
#   lcd = LCD(rs="PD4", en="PD5", d4="PD6", d5="PD7", d6="PB0", d7="PB1")
#   lcd.init()
#   lcd.clear()
#   lcd.print_str("Hello!")
#   lcd.set_cursor(col, row)
#   lcd.write_char(c)
#
# Hardware: 4-bit mode -- RS, EN, D4, D5, D6, D7 pins.
# RW tied low (write-only mode).
# Architecture-specific dispatch via _lcd/gpio.py.
from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint32, int32, inline, const


class LCD:

    @inline
    def __init__(self, rs: const[str], en: const[str], d4: const[str], d5: const[str], d6: const[str], d7: const[str]):
        self._rs = rs
        self._en = en
        self._d4 = d4
        self._d5 = d5
        self._d6 = d6
        self._d7 = d7

    @inline
    def init(self):
        match __CHIP__.arch:
            case "avr":
                from pymcu.drivers._lcd.gpio import lcd_init
                lcd_init(self._rs, self._en, self._d4, self._d5, self._d6, self._d7)

    @inline
    def clear(self):
        match __CHIP__.arch:
            case "avr":
                from pymcu.drivers._lcd.gpio import lcd_clear
                lcd_clear(self._rs, self._en, self._d4, self._d5, self._d6, self._d7)

    @inline
    def home(self):
        match __CHIP__.arch:
            case "avr":
                from pymcu.drivers._lcd.gpio import lcd_home
                lcd_home(self._rs, self._en, self._d4, self._d5, self._d6, self._d7)

    @inline
    def print_str(self, s: const[str]):
        match __CHIP__.arch:
            case "avr":
                from pymcu.drivers._lcd.gpio import lcd_print_str
                lcd_print_str(self._rs, self._en, self._d4, self._d5, self._d6, self._d7, s)

    @inline
    def set_cursor(self, col: uint8, row: uint8):
        match __CHIP__.arch:
            case "avr":
                from pymcu.drivers._lcd.gpio import lcd_set_cursor
                lcd_set_cursor(self._rs, self._en, self._d4, self._d5, self._d6, self._d7, col, row)

    @inline
    def write_char(self, c: uint8):
        match __CHIP__.arch:
            case "avr":
                from pymcu.drivers._lcd.gpio import lcd_write_char
                lcd_write_char(self._rs, self._en, self._d4, self._d5, self._d6, self._d7, c)

    @inline
    def print_fmt(self, value: int32, base: uint8, width: uint8, flags: uint8):
        # Backs f-string format specs in lcd.print_str(f"..."): formats `value` in `base`
        # (2/8/10/16) into a buffer, then writes it char-by-char. flags bit0=upper, bit1=signed,
        # bit2=zero-pad. Mirrors uart_write_fmt but emits through self.write_char (HD44780).
        zero_pad: uint8 = flags & 0x04
        pad: uint8 = 32          # ' '
        if zero_pad != 0:
            pad = 48             # '0'
        neg: uint8 = 0
        mag: uint32 = 0
        if (flags & 0x02) and value < 0:
            neg = 1
            mag = uint32(0 - value)
        else:
            mag = uint32(value)
        if neg != 0 and zero_pad != 0:
            self.write_char(45)  # '-'
            if width > 0:
                width = width - 1
        buf: uint8[16] = [0] * 16
        n: uint8 = 0
        if mag == 0:
            buf[0] = 48
            n = 1
        else:
            while mag > 0:
                d: uint8 = uint8(mag % base)
                if d < 10:
                    buf[n] = d + 48
                elif flags & 0x01:
                    buf[n] = d - 10 + 65
                else:
                    buf[n] = d - 10 + 97
                mag = mag // base
                n = n + 1
        if neg != 0 and zero_pad == 0:
            buf[n] = 45
            n = n + 1
        while n < width and n < 16:
            buf[n] = pad
            n = n + 1
        while n > 0:
            n = n - 1
            self.write_char(buf[n])
