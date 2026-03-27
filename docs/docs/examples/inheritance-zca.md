# Example: inheritance-zca

Demonstrates single-level ZCA class inheritance. The derived class extends the base with an
additional method. No SRAM is used — everything is inlined.

```python
from pymcu.hal.gpio import Pin
from pymcu.hal.uart import UART
from pymcu.types import uint8

class Sensor:
    @inline
    def __init__(self, pin: str):
        self._pin = Pin(pin, Pin.IN)
        self._value: uint8 = 0

    @inline
    def read(self) -> uint8:
        self._value = self._pin.value()
        return self._value

class LoggedSensor(Sensor):
    @inline
    def __init__(self, pin: str, uart_baud: uint16):
        self._uart = UART(uart_baud)
        Sensor.__init__(self, pin)

    @inline
    def read_and_log(self) -> uint8:
        v: uint8 = self.read()
        self._uart.write_str("val=")
        self._uart.print_byte(v)
        return v

def main():
    s = LoggedSensor("PD2", 9600)
    while True:
        v: uint8 = s.read_and_log()
```

## Key points

- `Sensor.__init__` is explicitly called from `LoggedSensor.__init__` — no automatic MRO.
- Both `_pin` and `_uart` are ZCA members: resolved at compile time, no SRAM instance struct.
- `self.read()` inside `read_and_log` resolves through the inherited method at compile time.
