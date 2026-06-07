from pymcu.chips import __CHIP__
from pymcu.types import uint8, uint16, inline, Callable, const
from pymcu.hal.pic18.pic18f45k50_adc import *

class AnalogPin:
    def __init__(self, channel: str):
        self.channel = channel
        adc_init(channel)

    @inline
    def start(self):
        adc_start(self.channel)

    @inline
    def read(self) -> uint16:
        return 0

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
