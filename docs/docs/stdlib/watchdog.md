# Watchdog — `whisnake.hal.watchdog`

```python
from whisnake.hal.watchdog import Watchdog
```

Hardware watchdog timer that resets the MCU if the firmware stops calling `feed()` within the
configured timeout. Useful for recovering from unexpected hang states in production firmware.

## `Watchdog(timeout_ms: const[uint16] = 500)`

The timeout must be a compile-time constant — it is resolved to a WDP prescaler value at build
time, so no runtime table lookup is needed.

### ATmega328P timeout table

| `timeout_ms` | WDP value | Actual timeout (~3V, 25 °C) |
|---|---|---|
| 16 | `0b000` | ~16 ms |
| 32 | `0b001` | ~32 ms |
| 64 | `0b010` | ~64 ms |
| 125 | `0b011` | ~125 ms |
| 250 | `0b100` | ~250 ms |
| 500 | `0b101` | ~500 ms |
| 1000 | `0b110` | ~1 s |
| 2000 | `0b111` | ~2 s |
| 4000 | `0b100_1` | ~4 s |
| 8000 | `0b101_1` | ~8 s |

### Methods

| Method | Description |
|---|---|
| `enable()` | Enable the watchdog with the configured timeout. |
| `disable()` | Disable the watchdog. Requires the timed-disable sequence (WDCE+WDE). |
| `feed()` | Reset the watchdog counter (`WDR` instruction). Must be called within the timeout. |

## Usage

```python
from whisnake.hal.watchdog import Watchdog
from whisnake.hal.uart import UART
from whisnake.types import uint8

def main():
    uart = UART(9600)
    wdt = Watchdog(timeout_ms=500)
    wdt.enable()

    while True:
        # Do work — must complete within 500 ms
        uart.write_str("alive\n")
        wdt.feed()
```

## Safety notes

- Call `wdt.feed()` from every code path that may take longer than the timeout (including ISRs
  that block the main loop).
- The watchdog persists across soft resets on ATmega328P. If `disable()` is not called at
  startup and the WDT flag is set in MCUSR, the device will reset in a loop. Clear `MCUSR`
  and call `disable()` in your startup sequence if you need to recover from this state.

## ATmega328P register map

| Register | Address | Description |
|---|---|---|
| `WDTCSR` | `0x60` | WD Timer Control/Status (WDP, WDEN, WDIE, WDIF, WDCE, WDE) |
| `MCUSR` | `0x54` | MCU Status Register (WDRF, BORF, EXTRF, PORF) |
