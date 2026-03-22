# Sleep / Power — `whipsnake.hal.power`

```python
from whipsnake.hal.power import sleep_power_down
```

Sleep-mode helpers that halt the CPU and reduce current consumption.  Each function sets the
appropriate sleep-mode bits, executes the `SLEEP` instruction, then clears the sleep-enable bit
on wake — so returning from the function means the MCU has woken.

!!! warning "Interrupts must be enabled"
    Global interrupts (`sei`) must be enabled before calling any sleep function, or the MCU will
    sleep forever.  The compiler emits `sei` automatically when at least one `@interrupt` handler
    is registered; otherwise call `asm("sei")` explicitly.

## Sleep modes

| Function | Current (3V, 25 °C) | Wake sources | Notes |
|---|---|---|---|
| `sleep_idle()` | ~6 mA | All interrupts | CPU halted; all peripherals still running |
| `sleep_adc_noise()` | ~1 mA | ADC complete, ext-int, TWI address match, Timer2 | Reduces ADC noise floor |
| `sleep_power_down()` | ~0.1 µA | External INT0/INT1, TWI address match, WDT | Deepest sleep; fastest wake |
| `sleep_power_save()` | ~0.1 µA | Same as power-down + Timer2 async | Timer2 keeps running (RTC use) |
| `sleep_standby()` | ~0.1 µA | Same as power-down | 6-cycle oscillator startup (faster than power-down) |

## Usage

### Interrupt-driven blink (power-down)

```python
from whipsnake.hal.gpio import Pin
from whipsnake.hal.power import sleep_power_down
from whipsnake.types import uint8, ptr

EICRA: ptr[uint8] = ptr(0x69)
EIMSK: ptr[uint8] = ptr(0x3D)

led = Pin("PB5", Pin.OUT)

@interrupt(0x0002)    # INT0 vector (ATmega328P)
def on_int0():
    led.toggle()

def main():
    EICRA[0] = 0    # INT0 on low level
    EIMSK[0] = 1    # enable INT0
    asm("sei")
    while True:
        sleep_power_down()   # wake on INT0, toggle LED, sleep again
```

### ADC noise reduction sleep

```python
from whipsnake.hal.adc import AnalogPin
from whipsnake.hal.power import sleep_adc_noise
from whipsnake.types import uint16

sensor = AnalogPin("A0")

def read_quiet() -> uint16:
    sensor.start()
    sleep_adc_noise()    # CPU sleeps; ADC conversion completes, ADC ISR wakes CPU
    return sensor.value()
```

### Watchdog-based periodic wake (ultra-low-power)

```python
from whipsnake.hal.power import sleep_power_down
from whipsnake.hal.watchdog import Watchdog

wdt = Watchdog(timeout_ms=1000)

@interrupt(0x0018)    # WDT vector (ATmega328P)
def on_wdt():
    pass    # just wake

def main():
    wdt.enable()
    asm("sei")
    while True:
        sleep_power_down()   # ~1 s @ ~0.1 µA, then do work
        do_periodic_task()
```

## ATmega328P register map

| Register | Address | Description |
|---|---|---|
| `SMCR` | `0x53` | Sleep Mode Control Register (SM2:0, SE) |
| `MCUCR` | `0x55` | MCU Control Register (BODS, BODSE) |
