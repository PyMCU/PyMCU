# HD44780 LCD GPIO implementation (4-bit mode, ATmega328P)
# All operations are @inline with direct match/case dispatch on pin names.
# Generates SBI/CBI instructions without any SRAM allocation.
from whipsnake.chips.atmega328p import DDRB, DDRC, DDRD, PORTB, PORTC, PORTD
from whipsnake.types import uint8, inline, const
from whipsnake.time import delay_ms, delay_us


@inline
def _bit_set(name: const[str]):
    # Set a pin output high via PORT register bit manipulation.
    match name:
        case 'PB0':
            PORTB[0] = 1
        case 'PB1':
            PORTB[1] = 1
        case 'PB2':
            PORTB[2] = 1
        case 'PB3':
            PORTB[3] = 1
        case 'PB4':
            PORTB[4] = 1
        case 'PB5':
            PORTB[5] = 1
        case 'PC0':
            PORTC[0] = 1
        case 'PC1':
            PORTC[1] = 1
        case 'PC2':
            PORTC[2] = 1
        case 'PC3':
            PORTC[3] = 1
        case 'PC4':
            PORTC[4] = 1
        case 'PC5':
            PORTC[5] = 1
        case 'PD0':
            PORTD[0] = 1
        case 'PD1':
            PORTD[1] = 1
        case 'PD2':
            PORTD[2] = 1
        case 'PD3':
            PORTD[3] = 1
        case 'PD4':
            PORTD[4] = 1
        case 'PD5':
            PORTD[5] = 1
        case 'PD6':
            PORTD[6] = 1
        case 'PD7':
            PORTD[7] = 1


@inline
def _bit_clr(name: const[str]):
    # Set a pin output low via PORT register bit manipulation.
    match name:
        case 'PB0':
            PORTB[0] = 0
        case 'PB1':
            PORTB[1] = 0
        case 'PB2':
            PORTB[2] = 0
        case 'PB3':
            PORTB[3] = 0
        case 'PB4':
            PORTB[4] = 0
        case 'PB5':
            PORTB[5] = 0
        case 'PC0':
            PORTC[0] = 0
        case 'PC1':
            PORTC[1] = 0
        case 'PC2':
            PORTC[2] = 0
        case 'PC3':
            PORTC[3] = 0
        case 'PC4':
            PORTC[4] = 0
        case 'PC5':
            PORTC[5] = 0
        case 'PD0':
            PORTD[0] = 0
        case 'PD1':
            PORTD[1] = 0
        case 'PD2':
            PORTD[2] = 0
        case 'PD3':
            PORTD[3] = 0
        case 'PD4':
            PORTD[4] = 0
        case 'PD5':
            PORTD[5] = 0
        case 'PD6':
            PORTD[6] = 0
        case 'PD7':
            PORTD[7] = 0


@inline
def _bit_set_ddr(name: const[str]):
    # Configure a pin as output via DDR register bit manipulation.
    match name:
        case 'PB0':
            DDRB[0] = 1
        case 'PB1':
            DDRB[1] = 1
        case 'PB2':
            DDRB[2] = 1
        case 'PB3':
            DDRB[3] = 1
        case 'PB4':
            DDRB[4] = 1
        case 'PB5':
            DDRB[5] = 1
        case 'PC0':
            DDRC[0] = 1
        case 'PC1':
            DDRC[1] = 1
        case 'PC2':
            DDRC[2] = 1
        case 'PC3':
            DDRC[3] = 1
        case 'PC4':
            DDRC[4] = 1
        case 'PC5':
            DDRC[5] = 1
        case 'PD0':
            DDRD[0] = 1
        case 'PD1':
            DDRD[1] = 1
        case 'PD2':
            DDRD[2] = 1
        case 'PD3':
            DDRD[3] = 1
        case 'PD4':
            DDRD[4] = 1
        case 'PD5':
            DDRD[5] = 1
        case 'PD6':
            DDRD[6] = 1
        case 'PD7':
            DDRD[7] = 1


@inline
def _lcd_nibble_send(d4: const[str], d5: const[str], d6: const[str], d7: const[str], en: const[str], val: uint8):
    if val & 1:
        _bit_set(d4)
    else:
        _bit_clr(d4)
    if val & 2:
        _bit_set(d5)
    else:
        _bit_clr(d5)
    if val & 4:
        _bit_set(d6)
    else:
        _bit_clr(d6)
    if val & 8:
        _bit_set(d7)
    else:
        _bit_clr(d7)
    _bit_set(en)
    delay_us(1)
    _bit_clr(en)
    delay_us(50)


@inline
def _lcd_send_byte_impl(rs: const[str], en: const[str], d4: const[str], d5: const[str], d6: const[str], d7: const[str], val: uint8, rs_level: uint8):
    if rs_level:
        _bit_set(rs)
    else:
        _bit_clr(rs)
    hi: uint8 = (val >> 4) & 0x0F
    _lcd_nibble_send(d4, d5, d6, d7, en, hi)
    lo: uint8 = val & 0x0F
    _lcd_nibble_send(d4, d5, d6, d7, en, lo)


@inline
def lcd_init(rs: const[str], en: const[str], d4: const[str], d5: const[str], d6: const[str], d7: const[str]):
    # Configure all pins as output
    _bit_set_ddr(rs)
    _bit_set_ddr(en)
    _bit_set_ddr(d4)
    _bit_set_ddr(d5)
    _bit_set_ddr(d6)
    _bit_set_ddr(d7)
    # Start with all pins low
    _bit_clr(rs)
    _bit_clr(en)
    _bit_clr(d4)
    _bit_clr(d5)
    _bit_clr(d6)
    _bit_clr(d7)
    # Power-on wait: >40ms
    delay_ms(50)
    # 4-bit init sequence (HD44780 datasheet): send 0x3 three times then 0x2
    _bit_clr(rs)
    _lcd_nibble_send(d4, d5, d6, d7, en, 0x3)
    delay_ms(5)
    _lcd_nibble_send(d4, d5, d6, d7, en, 0x3)
    delay_us(110)
    _lcd_nibble_send(d4, d5, d6, d7, en, 0x3)
    delay_us(110)
    _lcd_nibble_send(d4, d5, d6, d7, en, 0x2)
    delay_us(110)
    # Function set: 4-bit, 2-line, 5x8 font (0x28)
    _lcd_send_byte_impl(rs, en, d4, d5, d6, d7, 0x28, 0)
    delay_us(40)
    # Display on, cursor off, blink off (0x0C)
    _lcd_send_byte_impl(rs, en, d4, d5, d6, d7, 0x0C, 0)
    delay_us(40)
    # Clear display (0x01)
    _lcd_send_byte_impl(rs, en, d4, d5, d6, d7, 0x01, 0)
    delay_ms(2)
    # Entry mode: increment cursor, no shift (0x06)
    _lcd_send_byte_impl(rs, en, d4, d5, d6, d7, 0x06, 0)
    delay_us(40)


@inline
def lcd_clear(rs: const[str], en: const[str], d4: const[str], d5: const[str], d6: const[str], d7: const[str]):
    _lcd_send_byte_impl(rs, en, d4, d5, d6, d7, 0x01, 0)
    delay_ms(2)


@inline
def lcd_home(rs: const[str], en: const[str], d4: const[str], d5: const[str], d6: const[str], d7: const[str]):
    _lcd_send_byte_impl(rs, en, d4, d5, d6, d7, 0x02, 0)
    delay_ms(2)


@inline
def lcd_write_char(rs: const[str], en: const[str], d4: const[str], d5: const[str], d6: const[str], d7: const[str], c: uint8):
    _lcd_send_byte_impl(rs, en, d4, d5, d6, d7, c, 1)
    delay_us(40)


@inline
def lcd_set_cursor(rs: const[str], en: const[str], d4: const[str], d5: const[str], d6: const[str], d7: const[str], col: uint8, row: uint8):
    # DDRAM address: row 0 = 0x00, row 1 = 0x40, row 2 = 0x14, row 3 = 0x54
    addr: uint8 = col
    if row == 1:
        addr = col | 0x40
    elif row == 2:
        addr = col | 0x14
    elif row == 3:
        addr = col | 0x54
    _lcd_send_byte_impl(rs, en, d4, d5, d6, d7, 0x80 | addr, 0)
    delay_us(40)


@inline
def lcd_print_str(rs: const[str], en: const[str], d4: const[str], d5: const[str], d6: const[str], d7: const[str], s: const[str]):
    # for-in over const[str] is compile-time unrolled by the IR generator.
    for c in s:
        _lcd_send_byte_impl(rs, en, d4, d5, d6, d7, c, 1)
        delay_us(40)
