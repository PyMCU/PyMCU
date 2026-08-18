from pymcu.chips.pic10f200 import TRISGPIO, GPIO
from pymcu.exceptions import CompileError
from pymcu.types import uint8, inline

@inline
def pin_set_mode(name: str, mode: uint8):
    if name == "GP0":
        TRISGPIO[0] = mode
    elif name == "GP1":
        TRISGPIO[1] = mode
    elif name == "GP2":
        TRISGPIO[2] = mode
    elif name == "GP3":
        if mode == 0:
            raise CompileError("GP3 is input-only on the PIC10F200 and cannot be driven")
        TRISGPIO[3] = 1
    else:
        raise CompileError("PIC10F200 has GP0, GP1, GP2 and GP3 only")


@inline
def pin_high(name: str):
    if name == "GP0":
        GPIO[0] = 1
    elif name == "GP1":
        GPIO[1] = 1
    elif name == "GP2":
        GPIO[2] = 1
    elif name == "GP3":
        raise CompileError("GP3 is input-only on the PIC10F200 and cannot be driven")
    else:
        raise CompileError("PIC10F200 has GP0, GP1, GP2 and GP3 only")


@inline
def pin_low(name: str):
    if name == "GP0":
        GPIO[0] = 0
    elif name == "GP1":
        GPIO[1] = 0
    elif name == "GP2":
        GPIO[2] = 0
    elif name == "GP3":
        raise CompileError("GP3 is input-only on the PIC10F200 and cannot be driven")
    else:
        raise CompileError("PIC10F200 has GP0, GP1, GP2 and GP3 only")


@inline
def pin_toggle(name: str):
    if name == "GP0":
        GPIO[0] = GPIO[0] ^ 1
    elif name == "GP1":
        GPIO[1] = GPIO[1] ^ 1
    elif name == "GP2":
        GPIO[2] = GPIO[2] ^ 1
    elif name == "GP3":
        raise CompileError("GP3 is input-only on the PIC10F200 and cannot be driven")
    else:
        raise CompileError("PIC10F200 has GP0, GP1, GP2 and GP3 only")


@inline
def pin_read(name: str) -> uint8:
    if name == "GP0":
        return GPIO[0]
    elif name == "GP1":
        return GPIO[1]
    elif name == "GP2":
        return GPIO[2]
    elif name == "GP3":
        return GPIO[3]
    else:
        raise CompileError("PIC10F200 has GP0, GP1, GP2 and GP3 only")


@inline
def pin_write(name: str, val: uint8):
    if val == 1:
        pin_high(name)
    elif val == 0:
        pin_low(name)
    else:
        raise CompileError("a pin takes 0 or 1")


@inline
def pin_pull_up(name: str):
    raise CompileError("the PIC10F200 gates its pull-ups with NOT_GPPU, one bit for the whole port, and OPTION is write-only so it cannot be updated a bit at a time; a per-pin pull-up cannot be expressed on this core")


@inline
def pin_pull_off(name: str):
    raise CompileError("the PIC10F200 gates its pull-ups with NOT_GPPU, one bit for the whole port, and OPTION is write-only so it cannot be updated a bit at a time; a per-pin pull-up cannot be expressed on this core")
