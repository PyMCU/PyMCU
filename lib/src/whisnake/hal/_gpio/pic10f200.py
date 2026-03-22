from whisnake.chips.pic10f200 import TRISGPIO, GPIO, OPTION
from whisnake.types import uint8, inline

@inline
def pin_set_mode(name: str, mode: uint8):
    if name == "GP0":
        TRISGPIO[0] = mode
    elif name == "GP1":
        TRISGPIO[1] = mode
    elif name == "GP2":
        TRISGPIO[2] = mode

@inline
def pin_high(name: str):
    if name == "GP0":
        GPIO[0] = 1
    elif name == "GP1":
        GPIO[1] = 1
    elif name == "GP2":
        GPIO[2] = 1

@inline
def pin_low(name: str):
    if name == "GP0":
        GPIO[0] = 0
    elif name == "GP1":
        GPIO[1] = 0
    elif name == "GP2":
        GPIO[2] = 0

@inline
def pin_toggle(name: str):
    if name == "GP0":
        GPIO[0] = GPIO[0] ^ 1
    elif name == "GP1":
        GPIO[1] = GPIO[1] ^ 1
    elif name == "GP2":
        GPIO[2] = GPIO[2] ^ 1

@inline
def pin_read(name: str) -> uint8:
    if name == "GP0":
        return GPIO[0]
    elif name == "GP1":
        return GPIO[1]
    elif name == "GP2":
        return GPIO[2]

@inline
def pin_write(name: str, val: uint8):
    if name == "GP0":
        if val == 1:
            GPIO[0] = 1
        elif val == 0:
            GPIO[0] = 0
    elif name == "GP1":
        if val == 1:
            GPIO[1] = 1
        elif val == 0:
            GPIO[1] = 0
    elif name == "GP2":
        if val == 1:
            GPIO[2] = 1
        elif val == 0:
            GPIO[2] = 0

@inline
def pin_pull_up(name: str):
    if name == "GP0":
        OPTION[6] = 0
    elif name == "GP1":
        OPTION[6] = 0
    elif name == "GP2":
        raise NotImplementedError("Pull-up not available on GP2 for PIC10F200")

@inline
def pin_pull_off(name: str):
    if name == "GP0":
        OPTION[6] = 1
    elif name == "GP1":
        OPTION[6] = 1
    elif name == "GP2":
        raise NotImplementedError("Pull-up not available on GP2 for PIC10F200")
