# Time / Delays — `pymcu.time`

```python
from pymcu.time import delay_ms, delay_us
```

Busy-wait delay functions calibrated to the target clock frequency. No hardware timer is
consumed — delay loops use counted CPU instruction cycles.

---

## `delay_ms(ms: uint16)`

Busy-wait for approximately `ms` milliseconds.

- `ms` may be a runtime variable (`uint16`).
- Maximum: 65 535 ms (~65 seconds).
- On AVR at 16 MHz: 21 outer × 255 inner × 3 cycles ≈ 1 ms per iteration.

## `delay_us(us: uint8)`

Busy-wait for approximately `us` microseconds.

- `us` may be a runtime variable (`uint8`).
- Maximum: 255 µs.
- Accurate for `us >= 4` on ATmega328P at 16 MHz.

---

## Example

```python
from pymcu.time import delay_ms, delay_us
from pymcu.hal.gpio import Pin

def main():
    led = Pin("PB5", Pin.OUT)
    while True:
        led.high()
        delay_ms(500)
        led.low()
        delay_ms(500)
```

---

## Notes

- Interrupts are **not** disabled during delay loops. An ISR executing during a delay will
  extend it by the ISR duration.
- For precise, non-blocking timing, use hardware timer interrupts
  (`@interrupt` + Timer0/1/2).
- `delay_ms(0)` returns immediately without spinning.

---

## `millis()` and `micros()`

For elapsed-time measurements, use `millis()` and `micros()`:

```python
from pymcu.time import millis, micros
from pymcu.types import uint32

start: uint32 = millis()

# ... do work ...

elapsed: uint32 = millis() - start
```

These use a Timer0 overflow ISR to maintain a 32-bit counter. They require Timer0 to be
running with prescaler 64 (the default). The counter is read atomically under `CLI`/`SEI`.

At 16 MHz with prescaler 64 a Timer0 overflow takes **1024 µs**, not 1000 µs. `millis()`
does not return the raw overflow count: the ISR applies the Arduino-style fractional
correction — one millisecond per overflow plus 3/125 accumulated in eighths — so `millis()`
counts real milliseconds. `micros()` reads the raw overflow count together with `TCNT0` and
is monotonic across an overflow, so a difference of two readings is never negative.
