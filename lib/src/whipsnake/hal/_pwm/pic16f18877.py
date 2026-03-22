from whipsnake.chips.pic16f18877 import CCP1CON, CCPR1L, CCP2CON, CCPR2L, T2CON, T2PR, TRISC
from whipsnake.types import uint8, inline

@inline
def pwm_init(pin: str, duty: uint8):
    T2PR.value = 0xFF
    if pin == "RC2":
        TRISC[2] = 0
        CCPR1L.value = duty
        CCP1CON.value = 0x8F
    elif pin == "RC1":
        TRISC[1] = 0
        CCPR2L.value = duty
        CCP2CON.value = 0x8F
    T2CON.value = 0x80

@inline
def pwm_set_duty(pin: str, duty: uint8):
    if pin == "RC2":
        CCPR1L.value = duty
    elif pin == "RC1":
        CCPR2L.value = duty

@inline
def pwm_start(pin: str):
    T2CON[7] = 1
    if pin == "RC2":
        CCP1CON[7] = 1
    elif pin == "RC1":
        CCP2CON[7] = 1

@inline
def pwm_stop(pin: str):
    if pin == "RC2":
        CCP1CON[7] = 0
    elif pin == "RC1":
        CCP2CON[7] = 0
