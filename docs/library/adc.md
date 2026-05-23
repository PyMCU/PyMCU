# ADC — `pymcu.hal.adc`

```python
from pymcu.hal.adc import AnalogPin
```

Analog-to-digital conversion. Wraps the AVR ADC peripheral.

---

## class `AnalogPin`

### `AnalogPin(channel: str)`

Initializes the ADC for the given channel. On ATmega328P, `channel` is an Arduino analog pin
name (`"A0"` through `"A5"`) which maps to ADC channels 0–5 on PORTC.

### Methods

| Method | Return type | Description |
|---|---|---|
| `start()` | — | Begin a conversion (sets ADSC in ADCSRA) |
| `read() -> uint16` | `uint16` | 10-bit result (0–1023) |
| `read_u16() -> uint16` | `uint16` | 16-bit scaled result (0–65535) |
| `start_conversion()` | — | Start ADC with interrupt enabled |
| `read_result() -> uint16` | `uint16` | Read ADCL/ADCH result registers directly |
| `value() -> uint16` | `uint16` | Same as `read_result()` |

---

## Examples

### Polling conversion

```python
from pymcu.hal.adc import AnalogPin
from pymcu.types import ptr, uint8, uint16

ADCSRA: ptr[uint8] = ptr(0x7A)

def main():
    adc = AnalogPin("A0")
    while True:
        adc.start()
        while ADCSRA[6]:    # wait for ADSC to clear
            pass
        result: uint16 = adc.read()
        # result is 0-1023
```

### Scaled reading (16-bit)

```python
from pymcu.hal.adc import AnalogPin
from pymcu.types import uint16

adc = AnalogPin("A0")
adc.start()
# ... wait for conversion ...
val: uint16 = adc.read_u16()    # 0-65535 (10-bit × 64)
```

### Interrupt-driven

```python
from pymcu.hal.adc import AnalogPin
from pymcu.types import uint16

sensor = AnalogPin("A0")
result: uint16 = 0

@interrupt(0x002A)    # ADC Complete vector (ATmega328P)
def on_adc():
    global result
    result = sensor.read_result()

def main():
    sensor.start_conversion()
    while True:
        pass
```

---

## Internal temperature sensor

The ATmega328P has a built-in temperature sensor connected to ADC channel 8. No external
components are required.

```python
from pymcu.hal.adc import adc_read_temp_raw
from pymcu.types import uint16

def main():
    raw: uint16 = adc_read_temp_raw()
    # Factory calibration: ~314 counts at 25 °C, ~1 count per degree
    # No EEPROM calibration data — accuracy is ±10 °C typical
```

| Item | Detail |
|---|---|
| Import | `from pymcu.hal.adc import adc_read_temp_raw` |
| Return type | `uint16` — raw ADC count (channel 8, internal 1.1 V reference) |
| Typical value | ~314 at 25 °C |
| Scale | ~1 count / °C (uncalibrated) |
| Accuracy | ±10 °C typical (factory), ±2 °C with calibration |

For a rough Celsius estimate (calibration-free):

```python
temp_c: int = raw - 289    # offset for 25 °C baseline — adjust per chip
```

:::{note}
For accurate temperature readings use a calibrated external sensor such as
{doc}`DS18B20 <drivers/ds18b20>` or {doc}`DHT11 <drivers/dht11>`.
:::

---

## ATmega328P ADC register map

| Register | Address | Description |
|---|---|---|
| `ADMUX` | `0x7C` | MUX select + reference voltage |
| `ADCSRA` | `0x7A` | Control: ADEN, ADSC, ADIE, prescaler |
| `ADCSRB` | `0x7B` | Free-running mode |
| `ADCL` | `0x78` | Result low byte |
| `ADCH` | `0x79` | Result high byte |
