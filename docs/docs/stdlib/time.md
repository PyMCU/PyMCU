# Time / Delays — `pymcu.time`

```python
from whisnake.time import delay_ms, delay_us
```

Busy-wait delay functions. No hardware timer is consumed — the delay loops are calibrated to the
target clock frequency using counted instruction cycles.

## `delay_ms(ms: uint16)`

Busy-waits for approximately `ms` milliseconds.

- `ms` may be a runtime variable (`uint16`).
- On AVR at 16 MHz: 21 outer × 255 inner × 3 cycles ≈ 1 ms per iteration.
- Maximum: 65535 ms (~65 seconds).

## `delay_us(us: uint8)`

Busy-waits for approximately `us` microseconds.

- `us` may be a runtime variable (`uint8`).
- Maximum: 255 µs.

## Example

```python
from whisnake.time import delay_ms, delay_us
from whisnake.hal.gpio import Pin

def main():
    led = Pin("PB5", Pin.OUT)
    while True:
        led.high()
        delay_ms(500)
        led.low()
        delay_ms(500)
```

## Notes

- Interrupts are not disabled during delay loops. An ISR executing during the delay will extend it.
- For precise timing, use hardware timer interrupts (`@interrupt` + Timer0/1/2).
- `delay_us(n)` is accurate for `n >= 4` on ATmega328P at 16 MHz.
