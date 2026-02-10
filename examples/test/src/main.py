from pymcu.chips.pic16f877a import *

def led_blink_1():
    PORTB[RB0] = not PORTB[RB0]


def led_blink_2():
    PORTB[RB4] = not PORTB[RB4]


def main():
    while True:
        if PORTC[RC0]:
            continue
        led_blink_1()
        led_blink_2()
