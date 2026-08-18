from pymcu.chips.pic18f45k50 import CCP1CON, CCPR1L, CCP2CON, CCPR2L, T2CON, PR2, TRISC
from pymcu.chips import __FREQ__
from pymcu.types import uint8, uint16, inline


@inline
def pwm_t2con_for_freq(freq: uint16) -> uint8:
    if freq == 0:
        return 0x00
    match __FREQ__:
        case 16_000_000:
            if freq > 7812:
                return 0x00
            elif freq > 1953:
                return 0x01
            else:
                return 0x02
        case 8_000_000:
            if freq > 3906:
                return 0x00
            elif freq > 976:
                return 0x01
            else:
                return 0x02
        case 4_000_000:
            if freq > 1953:
                return 0x00
            elif freq > 488:
                return 0x01
            else:
                return 0x02
        case _:
            if freq > 7812:
                return 0x00
            elif freq > 1953:
                return 0x01
            else:
                return 0x02

@inline
def pwm_init(pin: str, duty: uint8, freq: uint16 = 0):
    PR2.value = 0xFF
    if pin == "RC2":
        TRISC[2] = 0
        CCPR1L.value = duty
        CCP1CON.value = 0x0C
    elif pin == "RC1":
        TRISC[1] = 0
        CCPR2L.value = duty
        CCP2CON.value = 0x0C
    T2CON.value = 0x04 | pwm_t2con_for_freq(freq)

@inline
def pwm_set_duty(pin: str, duty: uint8):
    if pin == "RC2":
        CCPR1L.value = duty
    elif pin == "RC1":
        CCPR2L.value = duty

@inline
def pwm_set_freq(pin: str, freq: uint16):
    T2CON.value = (T2CON.value & 0xFC) | pwm_t2con_for_freq(freq)


@inline
def pwm_start(pin: str):
    T2CON[2] = 1
    if pin == "RC2":
        CCP1CON.value = 0x0C
    elif pin == "RC1":
        CCP2CON.value = 0x0C

@inline
def pwm_stop(pin: str):
    if pin == "RC2":
        CCP1CON.value = 0x00
    elif pin == "RC1":
        CCP2CON.value = 0x00
