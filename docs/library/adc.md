# ADC — `pymcu.hal.adc`

```python
from pymcu.hal.adc import AnalogPin
```

Analog-to-digital conversion. Wraps the AVR ADC peripheral.

---

## class `AnalogPin`

### `AnalogPin(channel)`

Initializes the ADC for the given input. On the ATmega328P the input can be named four
ways, all equivalent: the register name `"PC0"`–`"PC5"`, the Arduino analog name
`"A0"`–`"A5"`, the Arduino board number `14`–`19`, or the converter's own channel number
`0`–`5`. The internal sources are `"TEMP"` / `"ADC8"` (die temperature sensor) and `"VBG"`
(the 1.1 V bandgap). On the ATtiny 25/45/85 the inputs are `"PB2"`/`"A1"`/`1`,
`"PB4"`/`"A2"`/`2`, `"PB3"`/`"A3"`/`3` and `"PB5"`/`"A0"`/`0`; the digital pin numbers are
not accepted there because they run in a different order from the channels.

A name with no converter behind it is refused where the `AnalogPin` is written. It used to
select channel 0 and say nothing, so a program that asked for a pin with no channel read
A0 forever, and `"A1"` through `"A5"` did the same.

### Methods

| Method | Return type | Description |
|---|---|---|
| `start()` | — | Begin a conversion (sets ADSC in ADCSRA) |
| `read() -> uint16` | `uint16` | 10-bit result (0–1023) |
| `read_u16() -> uint16` | `uint16` | Result scaled to the full 16-bit range (0–65535) |
| `start_conversion()` | — | Start ADC with interrupt enabled |
| `read_result() -> uint16` | `uint16` | Read ADCL/ADCH result registers directly |
| `irq(handler)` | — | Register an ISR at the ADC Complete vector and enable ADIE + global interrupts |
| `reference_millivolts() -> uint16` | `uint16` | The voltage the converter measures against, in millivolts (compile-time constant) |
| `reference_volts() -> float` | `float` | The same reference in volts, as a float literal |
| `measure_supply_millivolts() -> uint16` | `uint16` | The supply rail, **measured** against the 1.1 V bandgap; costs two conversions and leaves ADMUX on the bandgap |

---

## Examples

### Polling conversion

```python
from pymcu.hal.adc import AnalogPin
from pymcu.types import ptr, uint8, uint16

ADCSRA: ptr[uint8] = ptr(0x7A)

def main():
    adc = AnalogPin("PC0")      # or: from pymcu.boards.arduino_uno import A0
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

adc = AnalogPin("PC0")
adc.start()
# ... wait for conversion ...
val: uint16 = adc.read_u16()    # 0-65535; 1023 counts map to exactly 65535
```

The scaling replicates the top bits into the bottom ones rather than multiplying by 64,
which topped out at 65472 and left full scale on the pin 63 counts short of full scale in
the number.

### Turning a reading into volts

```python
from pymcu.hal.adc import AnalogPin

adc = AnalogPin("A0")
volts: float = adc.read_u16() * adc.reference_volts() / 65535.0
```

`reference_volts()` is what the converter measures against: the supply rail on the AVR and
PIC parts (`adc_init` selects AVcc), 3.3 V on the RP parts. It is a compile-time constant,
so the constants in that expression fold. On a board running an AVR at a rail other than
5 V, read the true value with `measure_supply_millivolts()`, which measures it against the
internal bandgap instead of trusting the constant.

### Interrupt-driven

```python
from pymcu.hal.adc import AnalogPin
from pymcu.types import uint16

sensor = AnalogPin("PC0")
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
from pymcu.hal.adc import AnalogPin
from pymcu.types import uint16

def main():
    temp = AnalogPin("TEMP")
    raw: uint16 = temp.read()
    # Factory calibration: ~314 counts at 25 °C, ~1 count per degree
    # No EEPROM calibration data — accuracy is ±10 °C typical
```

| Item | Detail |
|---|---|
| Channel | `AnalogPin("TEMP")` (`"ADC8"` is an alias) |
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
