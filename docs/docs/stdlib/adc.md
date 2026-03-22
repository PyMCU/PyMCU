# ADC — `whipsnake.hal.adc`

```python
from whipsnake.hal.adc import AnalogPin
```

## `AnalogPin(channel: str)`

Initializes the ADC for the given channel. On ATmega328P, `channel` is the Arduino analog pin
name (`"A0"` through `"A5"`) which maps to ADC channels 0-5 on `PORTC`.

### Methods

| Method | Description |
|---|---|
| `start()` | Begin an ADC conversion (sets ADSC in ADCSRA) |

After calling `start()`, poll `ADCSRA[6]` (ADSC bit) until it clears, then read the result
from `ADCL` and `ADCH`.

## Example

```python
from whipsnake.hal.adc import AnalogPin
from whipsnake.types import ptr, uint8, uint16

ADCSRA: ptr[uint8] = ptr(0x7A)
ADCL:   ptr[uint8] = ptr(0x78)
ADCH:   ptr[uint8] = ptr(0x79)

def main():
    adc = AnalogPin("A0")
    while True:
        adc.start()
        while ADCSRA[6]:    # wait for ADSC to clear
            pass
        lo: uint8 = ADCL.value
        hi: uint8 = ADCH.value
        result: uint16 = lo + hi * 256
        # use result (0-1023)
```

## ATmega328P ADC register map

| Register | Address | Description |
|---|---|---|
| `ADMUX` | `0x7C` | MUX select + reference voltage |
| `ADCSRA` | `0x7A` | Control: ADEN, ADSC, ADIE, prescaler |
| `ADCSRB` | `0x7B` | Free-running mode |
| `ADCL` | `0x78` | Result low byte |
| `ADCH` | `0x79` | Result high byte |
