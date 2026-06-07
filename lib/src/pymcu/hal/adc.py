from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError

if __CHIP__.arch == "avr":
    from pymcu.hal.avr.adc import AnalogPin
elif __CHIP__.arch == "pic14":
    from pymcu.hal.pic14.adc import AnalogPin
elif __CHIP__.arch == "pic18":
    from pymcu.hal.pic18.adc import AnalogPin
else:
    raise CompileError("ADC not supported on this architecture")
