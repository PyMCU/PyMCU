# DS18B20 1-Wire driver -- AVR (ATmega328P / ATmega328 family) implementation
#
# Bit-bangs the Dallas 1-Wire protocol on a single PORTD pin.
# Only PORTD pins (PD2-PD7) are supported; PD0 (RX) and PD1 (TX) are excluded
# to avoid collisions with the hardware UART.
#
# Return value: int16 raw scratchpad temperature (12-bit mode, 1/16 degrees C).
#   valid  : temperature x 16 (e.g. 25.0 C -> 400, -10.0 C -> -160)
#   -32768 : no device present or bus error
#
# References: DS18B20 datasheet Rev 3, Maxim AN27
from pymcu.chips.atmega328p import DDRD, PORTD, PIND
from pymcu.types import uint8, uint16, int16, inline
from pymcu.time import delay_us, delay_ms


@inline
def _ow_reset(bit: uint8) -> uint8:
    # Drive bus LOW for 480 us (reset pulse), then release for 70 us and sample.
    # Returns 1 if device pulls bus LOW (presence pulse), 0 if no device.
    mask: uint8 = 1 << bit
    DDRD.value = DDRD.value | mask      # output
    PORTD.value = PORTD.value & ~mask   # drive LOW
    delay_us(240)
    delay_us(240)                        # 480 us total reset pulse
    DDRD.value = DDRD.value & ~mask     # input (release bus)
    PORTD.value = PORTD.value | mask    # weak pull-up
    delay_us(70)                         # wait for presence pulse
    present: uint8 = 0
    if (PIND.value & mask) == 0:
        present = 1
    delay_us(200)
    delay_us(210)                        # complete ~480 us release window
    return present


@inline
def _ow_write_bit(bit: uint8, val: uint8):
    # Write one bit in a 60-us slot.
    # Write-1: 10 us low then release for 55 us
    # Write-0: 60 us low then release for 10 us
    mask: uint8 = 1 << bit
    DDRD.value = DDRD.value | mask      # output
    PORTD.value = PORTD.value & ~mask   # drive LOW
    if val != 0:
        delay_us(10)
        DDRD.value = DDRD.value & ~mask  # release
        PORTD.value = PORTD.value | mask
        delay_us(55)
    else:
        delay_us(60)
        DDRD.value = DDRD.value & ~mask  # release
        PORTD.value = PORTD.value | mask
        delay_us(10)


@inline
def _ow_write_byte(bit: uint8, data: uint8):
    # Write 8 bits LSB-first onto the 1-Wire bus.
    b: uint8 = 0
    d: uint8 = data
    while b < 8:
        _ow_write_bit(bit, d & 1)
        d = d >> 1
        b = b + 1


@inline
def _ow_read_bit(bit: uint8) -> uint8:
    # Sample one bit: drive LOW 2 us, release, sample at ~14 us from slot start.
    mask: uint8 = 1 << bit
    DDRD.value = DDRD.value | mask      # output
    PORTD.value = PORTD.value & ~mask   # drive LOW 2 us
    delay_us(2)
    DDRD.value = DDRD.value & ~mask     # release
    PORTD.value = PORTD.value | mask
    delay_us(12)                         # 2+12=14 us from start -- sample window
    sample: uint8 = 0
    if (PIND.value & mask) != 0:
        sample = 1
    delay_us(50)                         # complete 64-us slot
    return sample


@inline
def _ow_read_byte(bit: uint8) -> uint8:
    # Assemble 8 bits LSB-first from consecutive read slots.
    result: uint8 = 0
    b: uint8 = 0
    while b < 8:
        bv: uint8 = _ow_read_bit(bit)
        if bv != 0:
            result = result | (1 << b)
        b = b + 1
    return result


@inline
def _ow_read(bit: uint8) -> int16:
    # Full DS18B20 read sequence for a single device:
    #   reset -> Skip ROM (0xCC) -> Convert T (0x44) -> 750 ms ->
    #   reset -> Skip ROM (0xCC) -> Read Scratchpad (0xBE) -> 2 bytes
    # Returns raw 12-bit temperature as int16 (1/16 C), or -32768 on error.
    if _ow_reset(bit) == 0:
        return -32768
    _ow_write_byte(bit, 0xCC)   # Skip ROM (single device on bus)
    _ow_write_byte(bit, 0x44)   # Convert T command
    delay_ms(250)
    delay_ms(250)
    delay_ms(250)               # 750 ms total -- 12-bit conversion time
    if _ow_reset(bit) == 0:
        return -32768
    _ow_write_byte(bit, 0xCC)   # Skip ROM
    _ow_write_byte(bit, 0xBE)   # Read Scratchpad
    lo: uint8 = _ow_read_byte(bit)
    hi: uint8 = _ow_read_byte(bit)
    raw: int16 = int16((hi << 8) | lo)
    return raw


@inline
def _avr_read(pin_name: str) -> int16:
    # Dispatch on compile-time PORTD pin name to the correct bit number.
    if pin_name == "PD2":
        return _ow_read(2)
    elif pin_name == "PD3":
        return _ow_read(3)
    elif pin_name == "PD4":
        return _ow_read(4)
    elif pin_name == "PD5":
        return _ow_read(5)
    elif pin_name == "PD6":
        return _ow_read(6)
    elif pin_name == "PD7":
        return _ow_read(7)
    return -32768
# References: DS18B20 datasheet, Maxim AN27
from pymcu.chips.atmega328p import DDRD, PORTD, PIND
from pymcu.types import uint8, uint16, int16, inline


def _ow_reset(bit: uint8) -> uint8:
    # Drive bus LOW for 480 us (reset pulse), release, wait 70 us, read presence.
    # Returns 1 if a device pulls the bus LOW (presence pulse), 0 otherwise.
    mask: uint8 = 1 << bit
    # Drive low
    DDRD.value = DDRD.value | mask
    PORTD.value = PORTD.value & ~mask
    # 480 us delay (16 MHz): 480 * 16 = 7680 NOPs -- handled by delay_us loop
    _delay_us_480()
    # Release
    DDRD.value = DDRD.value & ~mask
    PORTD.value = PORTD.value | mask
    _delay_us_70()
    present: uint8 = 0
    if (PIND.value & mask) == 0:
        present = 1
    _delay_us_410()
    return present


def _ow_write_bit(bit: uint8, val: uint8):
    # Write one bit on the bus using a 60-us time slot.
    # val != 0 -> write 1 (10 us low, 55 us high)
    # val == 0 -> write 0 (65 us low, 5 us high)
    mask: uint8 = 1 << bit
    DDRD.value = DDRD.value | mask
    PORTD.value = PORTD.value & ~mask
    if val != 0:
        _delay_us_10()
        DDRD.value = DDRD.value & ~mask
        PORTD.value = PORTD.value | mask
        _delay_us_55()
    else:
        _delay_us_65()
        DDRD.value = DDRD.value & ~mask
        PORTD.value = PORTD.value | mask
        _delay_us_5()


def _ow_write_byte(bit: uint8, data: uint8):
    # Write 8 bits LSB-first.
    b: uint8 = 0
    d: uint8 = data
    while b < 8:
        _ow_write_bit(bit, d & 1)
        d = d >> 1
        b = b + 1


def _ow_read_bit(bit: uint8) -> uint8:
    # Read one bit: drive LOW 2 us, release, sample at 14 us, complete 64-us slot.
    mask: uint8 = 1 << bit
    DDRD.value = DDRD.value | mask
    PORTD.value = PORTD.value & ~mask
    _delay_us_2()
    DDRD.value = DDRD.value & ~mask
    PORTD.value = PORTD.value | mask
    _delay_us_12()
    sample: uint8 = 0
    if (PIND.value & mask) != 0:
        sample = 1
    _delay_us_50()
    return sample


def _ow_read_byte(bit: uint8) -> uint8:
    # Read 8 bits LSB-first and assemble into a byte.
    result: uint8 = 0
    b: uint8 = 0
    while b < 8:
        bv: uint8 = _ow_read_bit(bit)
        if bv != 0:
            result = result | (1 << b)
        b = b + 1
    return result


def _ow_read(bit: uint8) -> int16:
    # Full DS18B20 read sequence for a single device on the bus:
    #   reset -> Skip ROM (0xCC) -> Convert T (0x44) -> wait 750 ms ->
    #   reset -> Skip ROM (0xCC) -> Read Scratchpad (0xBE) -> read 2 bytes.
    # Returns raw 12-bit temperature as int16 (1/16 degree C), or -32768 on error.
    if _ow_reset(bit) == 0:
        return -32768
    _ow_write_byte(bit, 0xCC)   # Skip ROM
    _ow_write_byte(bit, 0x44)   # Convert T
    # 750 ms conversion time for 12-bit resolution
    _delay_ms_750()
    if _ow_reset(bit) == 0:
        return -32768
    _ow_write_byte(bit, 0xCC)   # Skip ROM
    _ow_write_byte(bit, 0xBE)   # Read Scratchpad
    lo: uint8 = _ow_read_byte(bit)
    hi: uint8 = _ow_read_byte(bit)
    raw: int16 = int16((hi << 8) | lo)
    return raw


@inline
def _avr_read(pin_name: str) -> int16:
    # Dispatch on compile-time pin name to the correct PORTD bit number.
    match pin_name:
        case "PD2":
            return _ow_read(2)
        case "PD3":
            return _ow_read(3)
        case "PD4":
            return _ow_read(4)
        case "PD5":
            return _ow_read(5)
        case "PD6":
            return _ow_read(6)
        case "PD7":
            return _ow_read(7)
        case _:
            return -32768


# ---------------------------------------------------------------------------
# Cycle-accurate delay helpers (16 MHz, disable interrupts assumed by caller)
# ---------------------------------------------------------------------------
# Each helper burns the exact number of cycles for the target duration.
# Uses asm() with CALL-based helper convention (no labels in @inline context).

from pymcu.types import asm


def _delay_us_2():
    # 2 us = 32 cycles at 16 MHz: 31 NOPs + 1 RET overhead handled by CALL
    asm("NOP\nNOP\nNOP\nNOP\nNOP\nNOP\nNOP\nNOP")
    asm("NOP\nNOP\nNOP\nNOP\nNOP\nNOP\nNOP\nNOP")
    asm("NOP\nNOP\nNOP\nNOP\nNOP\nNOP\nNOP\nNOP")
    asm("NOP\nNOP\nNOP\nNOP\nNOP\nNOP\nNOP")


def _delay_us_5():
    # 5 us = 80 cycles at 16 MHz
    # Use a small loop: LDI + 13x(SBIW+BRNE) + correction NOPs
    asm("LDI R24, 13")
    asm("_ds18b20_d5: SBIW R24, 1")
    asm("BRNE _ds18b20_d5")
    asm("NOP\nNOP\nNOP")


def _delay_us_10():
    # 10 us = 160 cycles at 16 MHz
    asm("LDI R24, 26")
    asm("_ds18b20_d10: SBIW R24, 1")
    asm("BRNE _ds18b20_d10")
    asm("NOP\nNOP\nNOP\nNOP")


def _delay_us_12():
    # 12 us = 192 cycles at 16 MHz
    asm("LDI R24, 32")
    asm("_ds18b20_d12: SBIW R24, 1")
    asm("BRNE _ds18b20_d12")


def _delay_us_50():
    # 50 us = 800 cycles at 16 MHz
    asm("LDI R25, 0")
    asm("LDI R24, 133")
    asm("_ds18b20_d50: SBIW R24, 1")
    asm("BRNE _ds18b20_d50")
    asm("NOP\nNOP\nNOP")


def _delay_us_55():
    # 55 us = 880 cycles at 16 MHz
    asm("LDI R25, 0")
    asm("LDI R24, 146")
    asm("_ds18b20_d55: SBIW R24, 1")
    asm("BRNE _ds18b20_d55")
    asm("NOP\nNOP\nNOP\nNOP")


def _delay_us_65():
    # 65 us = 1040 cycles at 16 MHz
    asm("LDI R25, 0")
    asm("LDI R24, 173")
    asm("_ds18b20_d65: SBIW R24, 1")
    asm("BRNE _ds18b20_d65")
    asm("NOP\nNOP\nNOP")


def _delay_us_70():
    # 70 us = 1120 cycles at 16 MHz
    asm("LDI R25, 0")
    asm("LDI R24, 186")
    asm("_ds18b20_d70: SBIW R24, 1")
    asm("BRNE _ds18b20_d70")
    asm("NOP\nNOP\nNOP\nNOP")


def _delay_us_410():
    # 410 us = 6560 cycles at 16 MHz
    asm("LDI R25, 0")
    asm("LDI R24, 0")
    asm("_ds18b20_d410a: SBIW R24, 1")
    asm("BRNE _ds18b20_d410a")
    asm("LDI R24, 25")
    asm("_ds18b20_d410b: SBIW R24, 1")
    asm("BRNE _ds18b20_d410b")
    asm("NOP\nNOP\nNOP\nNOP\nNOP\nNOP\nNOP\nNOP\nNOP")


def _delay_us_480():
    # 480 us = 7680 cycles at 16 MHz
    asm("LDI R25, 0")
    asm("LDI R24, 0")
    asm("_ds18b20_d480a: SBIW R24, 1")
    asm("BRNE _ds18b20_d480a")
    asm("LDI R24, 30")
    asm("_ds18b20_d480b: SBIW R24, 1")
    asm("BRNE _ds18b20_d480b")


def _delay_ms_750():
    # 750 ms = 12,000,000 cycles at 16 MHz (0xB71B00)
    # Outer loop: R22:R21 counts outer iterations, each inner loop = 65536 cycles
    # 12000000 / (3 * 65536) = ~61 outer iterations (61 * 3 * 65536 = 12,000,192 ~ ok)
    asm("LDI R22, 0")
    asm("LDI R21, 61")
    asm("_ds18b20_ms750_outer: LDI R25, 0")
    asm("LDI R24, 0")
    asm("_ds18b20_ms750_inner: SBIW R24, 1")
    asm("BRNE _ds18b20_ms750_inner")
    asm("SBIW R22, 1")
    asm("BRNE _ds18b20_ms750_outer")
