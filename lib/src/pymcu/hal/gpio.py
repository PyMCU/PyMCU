from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError

if __CHIP__.arch == "avr":
    from pymcu.hal.avr.gpio import Pin
elif __CHIP__.arch == "pic14":
    from pymcu.hal.pic14.gpio import Pin
elif __CHIP__.arch == "pic18":
    from pymcu.hal.pic14.gpio import Pin  # pic14/gpio.py acts as the general PIC gpio facade
elif __CHIP__.arch == "rp2040":
    from pymcu.hal.rp2040.gpio import Pin
else:
    raise CompileError("GPIO not supported on this architecture")
