# Sleep / Power — `pymcu.hal.power`

```python
from pymcu.hal.power import sleep_power_down
```

Sleep mode helpers that halt the CPU and reduce current consumption. Returning from any
`sleep_*` function means the MCU has woken from sleep.

:::{warning} Interrupts must be enabled
Global interrupts (`sei`) must be enabled before calling any sleep function, or the MCU will
sleep indefinitely. The compiler emits `sei` automatically when at least one `@interrupt`
handler is registered; otherwise call `asm("sei")` explicitly.
:::

---

## Sleep modes

| Function | Typical current (3 V, 25 °C) | Wake sources |
|---|---|---|
| `sleep_idle()` | ~6 mA | All interrupts |
| `sleep_adc_noise()` | ~1 mA | ADC complete, ext-int, TWI address match, Timer2 |
| `sleep_power_down()` | ~0.1 µA | External INT0/INT1, TWI address match, WDT |
| `sleep_power_save()` | ~0.1 µA | Same as power-down + Timer2 async |
| `sleep_standby()` | ~0.1 µA | Same as power-down (faster oscillator startup) |

---

## Examples

### Power-down on button press

```python
from pymcu.hal.gpio import Pin
from pymcu.hal.power import sleep_power_down
from pymcu.types import uint8, ptr

EICRA: ptr[uint8] = ptr(0x69)
EIMSK: ptr[uint8] = ptr(0x3D)

led = Pin("PB5", Pin.OUT)

@interrupt(0x0002)    # INT0 vector (ATmega328P)
def on_int0():
    led.toggle()

def main():
    EICRA[0] = 0    # INT0 triggered on low level
    EIMSK[0] = 1    # enable INT0
    asm("sei")
    while True:
        sleep_power_down()   # wake on INT0, toggle LED, sleep again
```

### ADC noise reduction sleep

```python
from pymcu.hal.adc import AnalogPin
from pymcu.hal.power import sleep_adc_noise
from pymcu.types import uint16

sensor = AnalogPin("A0")

def read_quiet() -> uint16:
    sensor.start()
    sleep_adc_noise()    # CPU sleeps; ADC completes, ADC ISR wakes CPU
    return sensor.value()
```

### Watchdog-based periodic wake (~0.1 µA between wakes)

```python
from pymcu.hal.power import sleep_power_down
from pymcu.hal.watchdog import Watchdog

wdt = Watchdog(timeout_ms=1000)

@interrupt(0x0018)    # WDT vector
def on_wdt():
    pass

def main():
    wdt.enable()
    asm("sei")
    while True:
        sleep_power_down()
        do_periodic_task()
```

---

## ATmega328P register map

| Register | Address | Description |
|---|---|---|
| `SMCR` | `0x53` | Sleep Mode Control Register (SM2:0, SE) |
| `MCUCR` | `0x55` | MCU Control Register (BODS, BODSE) |
