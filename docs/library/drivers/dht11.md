# DHT11 — `pymcu.drivers.dht11`

```python
from pymcu.drivers.dht11 import DHT11
```

Temperature and humidity sensor driver using the DHT11 1-wire protocol.

---

## class `DHT11`

### `DHT11(pin: str)`

Binds the driver to a GPIO pin at compile time. The pin must support both output (for the
start signal) and input (for reading the response).

### Methods

| Method | Return type | Description |
|---|---|---|
| `read() -> uint16` | `uint16` | Read temperature and humidity |

### Return value encoding

The 16-bit return value packs both readings:

| Bits | Contents |
|---|---|
| High byte (15–8) | Humidity (0–100 %) |
| Low byte (7–0) | Temperature in Celsius (0–50 °C) |
| `0xFFFF` | Read error: no response or checksum failure |

---

## Example

```python
from pymcu.drivers.dht11 import DHT11
from pymcu.hal.uart import UART
from pymcu.time import delay_ms
from pymcu.types import uint8, uint16

def main():
    sensor = DHT11("PD4")
    uart = UART(9600)

    while True:
        result: uint16 = sensor.read()
        if result != 0xFFFF:
            humidity: uint8 = result >> 8
            temp: uint8 = result & 0xFF
            uart.write_str("H=")
            uart.print_byte(humidity)
            uart.write_str(" T=")
            uart.print_byte(temp)
            uart.println("C")
        delay_ms(2000)
```

---

## Notes

- The DHT11 requires a **minimum 2-second interval** between reads. Calling `read()` more
  frequently will return stale data or a checksum error.
- The 1-wire protocol uses precise `delay_us()` timing. Avoid enabling ISRs that may fire
  during a read — they will corrupt the timing and cause checksum failures.
- The driver is architecture-independent. Pulse measurement delegates to
  `pymcu.drivers._dht11.avr` (or the appropriate arch sub-module) via `__CHIP__` dispatch.
