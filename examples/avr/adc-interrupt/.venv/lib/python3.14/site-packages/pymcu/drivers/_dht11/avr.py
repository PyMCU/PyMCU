# DHT11 AVR implementation — loaded by dht11.py via:
#   from pymcu.drivers._dht11.avr import _avr_read
# Pattern mirrors _gpio/atmega328p.py
from pymcu.types import uint8, uint16, inline
from pymcu.chips.atmega328p import DDRD, PORTD, PIND
from pymcu.time import delay_ms, delay_us


@inline
def _avr_read(pin_name: str) -> uint16:
    # Same if/elif pattern as pin_set_mode/pin_high in _gpio/atmega328p.py.
    # IRGenerator constant-folds string ID comparisons — only the matching
    # branch survives, identical to how all GPIO HAL dispatch works.
    if pin_name == "PD2":
        return _pd_read(2)
    elif pin_name == "PD3":
        return _pd_read(3)
    elif pin_name == "PD4":
        return _pd_read(4)
    elif pin_name == "PD5":
        return _pd_read(5)
    elif pin_name == "PD6":
        return _pd_read(6)
    elif pin_name == "PD7":
        return _pd_read(7)
    return 0xFFFF


def _pd_read(bit: uint8) -> uint16:
    mask: uint8 = 1 << bit

    # 1. Drive LOW 18 ms (start signal)
    DDRD.value  = DDRD.value  | mask
    PORTD.value = PORTD.value & ~mask
    delay_ms(18)

    # 2. Release + pull-up
    DDRD.value  = DDRD.value  & ~mask
    PORTD.value = PORTD.value | mask
    delay_us(40)

    # 3. Sensor ACK
    if _pd_wait(mask, 0) == 0:
        return 0xFFFF
    if _pd_wait(mask, 1) == 0:
        return 0xFFFF

    # 4. Read 5 bytes
    hum_int:  uint8 = _pd_byte(mask)
    hum_dec:  uint8 = _pd_byte(mask)
    temp_int: uint8 = _pd_byte(mask)
    temp_dec: uint8 = _pd_byte(mask)
    chksum:   uint8 = _pd_byte(mask)

    expected: uint8 = (hum_int + hum_dec + temp_int + temp_dec) & 0xFF
    if chksum != expected:
        return 0xFFFF

    result: uint16 = hum_int
    result = (result << 8) | temp_int
    return result


@inline
def _pd_wait(mask: uint8, level: uint8) -> uint8:
    timeout: uint8 = 255
    while timeout > 0:
        current: uint8 = PIND.value & mask
        if level == 0:
            if current == 0:
                return timeout
        else:
            if current != 0:
                return timeout
        timeout = timeout - 1
    return 0


@inline
def _pd_byte(mask: uint8) -> uint8:
    result: uint8 = 0
    bit: uint8 = 0
    while bit < 8:
        if _pd_wait(mask, 0) == 0:
            return 0
        if _pd_wait(mask, 1) == 0:
            return 0
        count: uint8 = 0
        while (PIND.value & mask) != 0:
            count = count + 1
            if count == 255:
                break
        result = result << 1
        if count > 35:
            result = result | 1
        bit = bit + 1
    return result
