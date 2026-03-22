from whisnake.chips.pic18f45k50 import CCP1CON, CCPR1L, CCP2CON, CCPR2L, T2CON, PR2, TRISC
from whisnake.types import uint8, inline

@inline
def pwm_init(pin: str, duty: uint8):
    PR2.value = 0xFF
    if pin == "RC2":
        TRISC[2] = 0
        CCPR1L.value = duty
        CCP1CON.value = 0x0C
    elif pin == "RC1":
        TRISC[1] = 0
        CCPR2L.value = duty
        CCP2CON.value = 0x0C
    T2CON.value = 0x04

@inline
def pwm_set_duty(pin: str, duty: uint8):
    if pin == "RC2":
        CCPR1L.value = duty
    elif pin == "RC1":
        CCPR2L.value = duty

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
