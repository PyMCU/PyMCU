from whisnake.chips.pic16f877a import *

PWM_PIN_BIT = 2

system_tick = 0
pwm_direction = 0
current_duty = 0


@interrupt
def isr_handler():
    if PIR1[0]:
        PIR1[0] = 0

        TMR1.value = 0xFC18

        global system_tick
        system_tick = system_tick + 1


@inline
def setup_pwm():
    TRISC[2] = 0
    PR2.value = 0xFF
    T2CON.value = 0x04
    CCP1CON.value = 0x0C
    CCPR1L.value = 0


@inline
def setup_timer1():
    T1CON.value = 0x31
    TMR1.value = 0xFC18


@inline
def enable_interrupts():
    PIE1[0] = 1
    INTCON[6] = 1
    INTCON[7] = 1


def update_breathing():
    global current_duty, pwm_direction

    if pwm_direction == 0:

        current_duty = current_duty + 5
        if current_duty > 250:
            pwm_direction = 1
    else:

        current_duty = current_duty - 5
        if current_duty < 5:
            pwm_direction = 0


    CCPR1L.value = current_duty

def main():
    setup_pwm()
    setup_timer1()
    enable_interrupts()

    last_tick = 0

    state = 0

    while True:
        if system_tick != last_tick:
            last_tick = system_tick

            match state:
                case 0:
                    update_breathing()

                    if not PORTB[0]:
                        state = 1

                case 1:
                    if system_tick & 1:
                        CCPR1L.value = 255
                    else:
                        CCPR1L.value = 0

                    if PORTB[0]:
                        state = 0