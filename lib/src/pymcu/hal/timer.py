from pymcu.chips import __CHIP__
from pymcu.types import uint8, inline

class Timer0:
    @inline
    def __init__(self: uint8, prescaler: uint8):
        self.prescaler = prescaler
        if __CHIP__.name == "pic16f877a":
            from pymcu.hal._timer.pic16f877a import timer0_init
            timer0_init(prescaler)
        elif __CHIP__.name == "pic16f18877":
            from pymcu.hal._timer.pic16f18877 import timer0_init
            timer0_init(prescaler)
        elif __CHIP__.name == "pic16f84a":
            from pymcu.hal._timer.pic16f84a import timer0_init
            timer0_init(prescaler)
        elif __CHIP__.name == "pic10f200":
            from pymcu.hal._timer.pic10f200 import timer0_init
            timer0_init(prescaler)
        elif __CHIP__.name == "pic18f45k50":
            from pymcu.hal._timer.pic18f45k50 import timer0_init
            timer0_init(prescaler)
        elif __CHIP__.name == "atmega328p":
            from pymcu.hal._timer.atmega328p import timer0_init
            timer0_init(prescaler)

    @inline
    def start(self: uint8):
        if __CHIP__.name == "pic16f877a":
            from pymcu.hal._timer.pic16f877a import timer0_start
            timer0_start()
        elif __CHIP__.name == "pic16f18877":
            from pymcu.hal._timer.pic16f18877 import timer0_start
            timer0_start()
        elif __CHIP__.name == "pic16f84a":
            from pymcu.hal._timer.pic16f84a import timer0_start
            timer0_start()
        elif __CHIP__.name == "pic10f200":
            from pymcu.hal._timer.pic10f200 import timer0_start
            timer0_start()
        elif __CHIP__.name == "pic18f45k50":
            from pymcu.hal._timer.pic18f45k50 import timer0_start
            timer0_start()
        elif __CHIP__.name == "atmega328p":
            from pymcu.hal._timer.atmega328p import timer0_start
            timer0_start()

    @inline
    def stop(self: uint8):
        if __CHIP__.name == "pic16f877a":
            from pymcu.hal._timer.pic16f877a import timer0_stop
            timer0_stop()
        elif __CHIP__.name == "pic16f18877":
            from pymcu.hal._timer.pic16f18877 import timer0_stop
            timer0_stop()
        elif __CHIP__.name == "pic16f84a":
            from pymcu.hal._timer.pic16f84a import timer0_stop
            timer0_stop()
        elif __CHIP__.name == "pic10f200":
            from pymcu.hal._timer.pic10f200 import timer0_stop
            timer0_stop()
        elif __CHIP__.name == "pic18f45k50":
            from pymcu.hal._timer.pic18f45k50 import timer0_stop
            timer0_stop()
        elif __CHIP__.name == "atmega328p":
            from pymcu.hal._timer.atmega328p import timer0_stop
            timer0_stop()

    @inline
    def clear(self: uint8):
        if __CHIP__.name == "pic16f877a":
            from pymcu.hal._timer.pic16f877a import timer0_clear
            timer0_clear()
        elif __CHIP__.name == "pic16f18877":
            from pymcu.hal._timer.pic16f18877 import timer0_clear
            timer0_clear()
        elif __CHIP__.name == "pic16f84a":
            from pymcu.hal._timer.pic16f84a import timer0_clear
            timer0_clear()
        elif __CHIP__.name == "pic10f200":
            from pymcu.hal._timer.pic10f200 import timer0_clear
            timer0_clear()
        elif __CHIP__.name == "pic18f45k50":
            from pymcu.hal._timer.pic18f45k50 import timer0_clear
            timer0_clear()
        elif __CHIP__.name == "atmega328p":
            from pymcu.hal._timer.atmega328p import timer0_clear
            timer0_clear()
