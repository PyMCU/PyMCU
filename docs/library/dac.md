# DAC — `pymcu.hal.dac`

```python
from pymcu.hal.dac import DACPin
```

Digital-to-analog output: a pin that **drives** a voltage, the mirror of
{doc}`pymcu.hal.adc <adc>`, which reads one.

---

## class `DACPin`

### `DACPin(pin)`

Claims the pin and powers up the converter behind it.

**No part PyMCU targets today has one.** The constructor refuses where it is written and
names what does work on that part. That refusal is the point of the module: before it,
`analogio.AnalogOut` built with a warning and compiled the assignment to nothing, so a
program that asked for an analog output ran and drove no pin.

| Family | Answer |
|---|---|
| AVR | Refused. No AVR part has a converter. |
| PIC12 / PIC14 | Refused. |
| PIC18 | Refused. The PIC18F45K50's 5-bit reference DAC feeds the comparators, not a pin. |
| RP2040 / RP2350 | Refused. |

### Methods

| Method | Return type | Description |
|---|---|---|
| `set_value_u16(value: uint16)` | — | Drive the pin at `value` of full scale, 0–65535 — the same unit `pymcu.hal.adc` reads and `pymcu.hal.pwm` takes |
| `deinit()` | — | Release the pin |

---

## What to use instead

For an analog-like output on a part with no converter, filter a PWM wave:

```python
from pymcu.hal.pwm import PWM

out = PWM("PD6", freq=62500, duty_u16=32768)   # half the supply after the filter
out.set_duty_u16(49152)                        # three quarters
```

A first-order RC on the pin turns the square wave into a level. Pick the corner well below
the PWM frequency: at 62.5 kHz, 10 kΩ and 100 nF (160 Hz corner) leaves a few millivolts of
ripple. The trade is bandwidth — the output follows changes no faster than the filter.

Where a real converter is required (audio, precision references), drive an external one
over {doc}`SPI <spi>` or {doc}`I2C <i2c>`.

---

## Adding a part that has one

A new branch in `DACPin.__init__` plus a chip module with `dac_init` and `dac_write_u16`.
Nothing above `pymcu/hal/dac.py` changes: the compatibility layers speak `set_value_u16`
and never learn the part's resolution.
