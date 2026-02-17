from pymcu.chips import __CHIP__
from pymcu.types import uint8, inline

class PWM:
    @inline
    def __init__(self: uint8, pin: str, duty: uint8):
        self.pin = pin
        if __CHIP__.name == "pic16f877a":
            from pymcu.hal._pwm.pic16f877a import pwm_init
            pwm_init(pin, duty)
        elif __CHIP__.name == "pic16f18877":
            from pymcu.hal._pwm.pic16f18877 import pwm_init
            pwm_init(pin, duty)
        elif __CHIP__.name == "pic18f45k50":
            from pymcu.hal._pwm.pic18f45k50 import pwm_init
            pwm_init(pin, duty)
        elif __CHIP__.name == "atmega328p":
            from pymcu.hal._pwm.atmega328p import pwm_init
            pwm_init(pin, duty)

    @inline
    def set_duty(self: uint8, duty: uint8):
        if __CHIP__.name == "pic16f877a":
            from pymcu.hal._pwm.pic16f877a import pwm_set_duty
            pwm_set_duty(self.pin, duty)
        elif __CHIP__.name == "pic16f18877":
            from pymcu.hal._pwm.pic16f18877 import pwm_set_duty
            pwm_set_duty(self.pin, duty)
        elif __CHIP__.name == "pic18f45k50":
            from pymcu.hal._pwm.pic18f45k50 import pwm_set_duty
            pwm_set_duty(self.pin, duty)
        elif __CHIP__.name == "atmega328p":
            from pymcu.hal._pwm.atmega328p import pwm_set_duty
            pwm_set_duty(self.pin, duty)

    @inline
    def start(self: uint8):
        if __CHIP__.name == "pic16f877a":
            from pymcu.hal._pwm.pic16f877a import pwm_start
            pwm_start(self.pin)
        elif __CHIP__.name == "pic16f18877":
            from pymcu.hal._pwm.pic16f18877 import pwm_start
            pwm_start(self.pin)
        elif __CHIP__.name == "pic18f45k50":
            from pymcu.hal._pwm.pic18f45k50 import pwm_start
            pwm_start(self.pin)
        elif __CHIP__.name == "atmega328p":
            from pymcu.hal._pwm.atmega328p import pwm_start
            pwm_start(self.pin)

    @inline
    def stop(self: uint8):
        if __CHIP__.name == "pic16f877a":
            from pymcu.hal._pwm.pic16f877a import pwm_stop
            pwm_stop(self.pin)
        elif __CHIP__.name == "pic16f18877":
            from pymcu.hal._pwm.pic16f18877 import pwm_stop
            pwm_stop(self.pin)
        elif __CHIP__.name == "pic18f45k50":
            from pymcu.hal._pwm.pic18f45k50 import pwm_stop
            pwm_stop(self.pin)
        elif __CHIP__.name == "atmega328p":
            from pymcu.hal._pwm.atmega328p import pwm_stop
            pwm_stop(self.pin)
