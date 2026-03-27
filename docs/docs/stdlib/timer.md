# Timer — `pymcu.hal.timer`

```python
from pymcu.hal.timer import Timer0
```

## `Timer0(prescaler: uint8)`

Configures Timer/Counter 0 with the given prescaler. On ATmega328P, valid prescaler values are:

| Value | Prescaler | Period (16 MHz) |
|---|---|---|
| 1 | /1 | 16 ns per tick |
| 8 | /8 | 128 ns per tick |
| 64 | /64 | 1 µs per tick |
| 256 | /256 | 4 µs per tick |
| 1024 | /1024 | 16 µs per tick |

### Methods

| Method | Description |
|---|---|
| `start()` | Enable the timer |
| `stop()` | Disable the timer (prescaler = 0) |
| `clear()` | Reset TCNT0 to 0 |

## Timer interrupt

Use `@interrupt(vector)` to handle overflow interrupts:

```python
from pymcu.hal.timer import Timer0
from pymcu.types import uint8, ptr

TIMSK0: ptr[uint8] = ptr(0x6E)

tick: uint8 = 0

@interrupt(0x0020)   # Timer0 OVF vector on ATmega328P
def on_timer0_ovf():
    global tick
    tick += 1

def main():
    t = Timer0(prescaler=64)
    TIMSK0[1] = 1    # enable TOIE0
    t.start()
    # sei is called automatically when @interrupt handler is registered
    while True:
        pass
```

## ATmega328P Timer0 register map

| Register | Address | Description |
|---|---|---|
| `TCCR0A` | `0x44` | Waveform generation mode |
| `TCCR0B` | `0x45` | Clock select (prescaler) |
| `TCNT0` | `0x46` | Counter value |
| `OCR0A` | `0x47` | Output Compare A |
| `OCR0B` | `0x48` | Output Compare B |
| `TIMSK0` | `0x6E` | Interrupt mask (TOIE0, OCIE0A, OCIE0B) |
| `TIFR0` | `0x35` | Interrupt flag register |
