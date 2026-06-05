from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint16, inline, Callable, const

if __CHIP__.name == "pic16f877a":
    from pymcu.hal.pic14.pic16f877a_adc import *
elif __CHIP__.name == "pic16f18877":
    from pymcu.hal.pic14.pic16f18877_adc import *

class AnalogPin:
    def __init__(self, channel: str):
        self.channel = channel
        adc_init(channel)

    @inline
    def start(self):
        adc_start(self.channel)

    @inline
    def read(self) -> uint16:
        return 0  # not fully implemented in old pic14 adc

    @inline
    def start_conversion(self):
        pass

    @inline
    def read_result(self) -> uint16:
        return 0

    @inline
    def irq(self, handler: Callable):
        pass

    @inline
    def read_u16(self) -> uint16:
        return 0
