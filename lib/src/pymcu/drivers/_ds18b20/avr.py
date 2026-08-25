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
from pymcu.exceptions import CompileError
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
    else:
        # This used to return the driver's bus-error sentinel, so a pin the driver
        # cannot drive built a program with no 1-Wire protocol in it and reported
        # the value a missing sensor reports.
        raise CompileError("DS18B20: unsupported data pin -- use PD2-PD7")
