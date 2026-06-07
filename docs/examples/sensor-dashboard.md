# Example: Sensor Dashboard

Reads temperature and humidity from a DHT11 sensor every 2 seconds and prints the readings
over UART in a human-readable format.

**Flash:** ~800 bytes &ensp; **SRAM:** 4 bytes

---

## Source

```python
from pymcu.drivers.dht11 import DHT11
from pymcu.hal.uart import UART
from pymcu.time import delay_ms
from pymcu.types import uint8, uint16

def main():
    sensor = DHT11("PD4")
    uart = UART(9600)
    uart.println("DHT11 Dashboard")

    while True:
        result: uint16 = sensor.read()

        if result == 0xFFFF:
            uart.println("sensor error")
        else:
            humidity: uint8 = result >> 8
            temp: uint8 = result & 0xFF

            uart.write_str("temp=")
            uart.print_byte(temp)
            uart.write_str(" hum=")
            uart.print_byte(humidity)

        delay_ms(2000)
```

---

## How it compiles

- `DHT11("PD4")` binds the driver to a compile-time pin constant. No SRAM is used for the
  driver object.
- The 1-wire read protocol uses precise `delay_us()` timing — the delays are resolved to
  CPU cycle counts at compile time for the target clock frequency.
- `result >> 8` and `result & 0xFF` compile to single AVR instructions (`MOV`/`AND`).
- `uart.print_byte(temp)` formats the byte as decimal digits using a compile-time-unrolled
  division loop — no `sprintf`, no format string at runtime.
- The only SRAM used is the `result`, `humidity`, and `temp` variables (4 bytes total, likely
  held in registers throughout).

---

## Build and flash

```bash
cd examples/avr/sensor_dashboard
pymcu build
pymcu flash --port /dev/cu.usbmodem*
```

Wire DHT11 DATA to Arduino pin D4 (PD4), VCC to 3.3–5 V, GND to GND. Open a serial
monitor at 9600 baud.

---

## Wiring

```
Arduino Uno         DHT11
-----------         -----
PD4 (D4)    ←→     DATA
5V          ←→     VCC
GND         ←→     GND
```

Add a 10 kΩ pull-up resistor between DATA and VCC.
