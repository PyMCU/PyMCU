# WiFi HAL facade: dispatch to the per-chip CYW43439 driver. Only the Pico 2 W (rp2350)
# path is wired today.
from pymcu.chips import __CHIP__
from pymcu.exceptions import CompileError

if __CHIP__.name == "rp2350":
    from pymcu.hal.rp2350.cyw43 import CYW43
else:
    raise CompileError("WiFi (CYW43439) is only supported on the Pico 2 W (rp2350) so far")
