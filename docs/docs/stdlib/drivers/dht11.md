# DHT11 Sensor — `pymcu.drivers.dht11`

```python
from pymcu.drivers.dht11 import DHT11
```

Driver for the DHT11 temperature and humidity sensor.

## `DHT11(pin: str)`

Binds the driver to a GPIO pin at compile time.

### Methods

| Method | Return type | Description |
|---|---|---|
| `read() -> uint16` | `uint16` | Read temperature and humidity |

The return value packs both readings into a 16-bit integer:
- **High byte:** humidity (0-100%)
- **Low byte:** temperature in Celsius (0-50°C)
- **`0xFFFF`:** read error (no response or checksum failure)

## Example

```python
from pymcu.drivers.dht11 import DHT11
from whipsnake.hal.uart import UART
from whipsnake.types import uint8, uint16

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
            uart.write_str("T=")
            uart.print_byte(temp)
        delay_ms(2000)
```

## Notes

- The DHT11 requires a minimum 2-second interval between reads.
- The 1-wire protocol uses precise `delay_us()` timing; avoid enabling ISRs during reads.
- The driver is target-independent; architecture-specific pulse measurement is in
  `pymcu.drivers._dht11.avr`.
