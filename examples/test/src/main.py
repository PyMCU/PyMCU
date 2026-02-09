from pymcu.chips.pic16f877a import *

def main():
    while True:
        PORTB[RB0] = not PORTB[RB0]
