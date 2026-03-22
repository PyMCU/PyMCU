# DHT11 temperature & humidity sensor driver
# Zero-cost abstraction — mirrors the Pin/UART pattern exactly.
#
# Usage:
#   from whisnake.drivers.dht11 import DHT11
#   from whisnake.boards.arduino_uno import D2
#
#   sensor = DHT11(D2)       # compile-time pin binding
#   data   = sensor.read()   # uint16: high=humidity%, low=temp C | 0xFFFF=error
#
# The class interface is arch-neutral.  Architecture-specific dispatch is done
# via the same two-level DCE used by Pin and UART:
#   1. match __CHIP__.arch  — eliminates non-matching architectures
#   2. _avr_read(self.name) — const[str] dispatch, eliminates non-matching pins
#
# To add a new architecture, add a new case to DHT11.read() and create
# lib/src/pymcu/drivers/_dht11/<arch>.py following the avr.py template.
from whisnake.chips import __CHIP__
from whisnake.types import uint8, uint16, inline


class DHT11:

    @inline
    def __init__(self, pin: str):
        self.name = pin

    @inline
    def read(self) -> uint16:
        match __CHIP__.arch:
            case "avr":
                from whisnake.drivers._dht11.avr import _avr_read
                return _avr_read(self.name)
            case _:
                return 0xFFFF
