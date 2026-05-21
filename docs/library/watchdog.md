# Watchdog — `pymcu.hal.watchdog`

```python
from pymcu.hal.watchdog import Watchdog
```

Hardware watchdog timer. Resets the MCU if firmware stops calling `feed()` within the
configured timeout — useful for recovering from unexpected hang states in production firmware.

---

## class `Watchdog`

### `Watchdog(timeout_ms: const[uint16] = 500)`

The timeout must be a compile-time constant — it is resolved to a WDP prescaler at build time.

### Timeout table (ATmega328P, ~3 V, 25 °C)

| `timeout_ms` | WDP | Actual timeout |
|---|---|---|
| 16 | `0b000` | ~16 ms |
| 32 | `0b001` | ~32 ms |
| 64 | `0b010` | ~64 ms |
| 125 | `0b011` | ~125 ms |
| 250 | `0b100` | ~250 ms |
| 500 | `0b101` | ~500 ms |
| 1000 | `0b110` | ~1 s |
| 2000 | `0b111` | ~2 s |
| 4000 | `0b1000` | ~4 s |
| 8000 | `0b1001` | ~8 s |

### Methods

| Method | Description |
|---|---|
| `enable()` | Enable the watchdog with the configured timeout |
| `disable()` | Disable the watchdog (timed-disable sequence: WDCE+WDE) |
| `feed()` | Reset the watchdog counter (`WDR` instruction) |

---

## Example

```python
from pymcu.hal.watchdog import Watchdog
from pymcu.hal.uart import UART

def main():
    uart = UART(9600)
    wdt = Watchdog(timeout_ms=500)
    wdt.enable()

    while True:
        uart.write_str("alive\n")
        wdt.feed()      # must be called within 500 ms
```

---

## Safety notes

- Call `wdt.feed()` on **every code path** that may take longer than the timeout (including
  ISRs that block the main loop).
- The watchdog persists across soft resets on ATmega328P. If `disable()` is not called at
  startup and the WDT flag is set in `MCUSR`, the device will reset in a loop. Clear `MCUSR`
  and call `disable()` at the top of `main()` to recover from this state.

---

## Watchdog-based ultra-low-power wake

Combine with the power module for periodic wake from deep sleep:

```python
from pymcu.hal.power import sleep_power_down
from pymcu.hal.watchdog import Watchdog

wdt = Watchdog(timeout_ms=1000)

@interrupt(0x0018)    # WDT vector (ATmega328P)
def on_wdt():
    pass              # just wake; do nothing in ISR

def main():
    wdt.enable()
    asm("sei")
    while True:
        sleep_power_down()   # ~1 s @ ~0.1 µA, then run loop body
        do_periodic_task()
```

---

## ATmega328P register map

| Register | Address | Description |
|---|---|---|
| `WDTCSR` | `0x60` | WD Control/Status (WDP, WDEN, WDIE, WDIF, WDCE, WDE) |
| `MCUSR` | `0x54` | MCU Status Register (WDRF, BORF, EXTRF, PORF) |
