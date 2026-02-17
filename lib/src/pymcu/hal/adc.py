from pymcu.chips import __CHIP__
from pymcu.types import uint8, inline

class AnalogPin:
    @inline
    def __init__(self: uint8, channel: str):
        self.channel = channel
        if __CHIP__.name == "pic16f877a":
            from pymcu.hal._adc.pic16f877a import adc_init
            adc_init(channel)
        elif __CHIP__.name == "pic16f18877":
            from pymcu.hal._adc.pic16f18877 import adc_init
            adc_init(channel)
        elif __CHIP__.name == "pic18f45k50":
            from pymcu.hal._adc.pic18f45k50 import adc_init
            adc_init(channel)
        elif __CHIP__.name == "atmega328p":
            from pymcu.hal._adc.atmega328p import adc_init
            adc_init(channel)

    @inline
    def start(self: uint8):
        if __CHIP__.name == "pic16f877a":
            from pymcu.hal._adc.pic16f877a import adc_start
            adc_start(self.channel)
        elif __CHIP__.name == "pic16f18877":
            from pymcu.hal._adc.pic16f18877 import adc_start
            adc_start(self.channel)
        elif __CHIP__.name == "pic18f45k50":
            from pymcu.hal._adc.pic18f45k50 import adc_start
            adc_start(self.channel)
        elif __CHIP__.name == "atmega328p":
            from pymcu.hal._adc.atmega328p import adc_start
            adc_start(self.channel)
