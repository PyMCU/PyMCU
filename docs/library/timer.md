# Timer — `pymcu.hal.timer`

```python
from pymcu.hal.timer import Timer
```

Hardware timer configuration. Wraps Timer/Counter 0, 1, and 2 on ATmega328P.

---

## class `Timer`

### `Timer(n: uint8, prescaler: uint16)`

Configures a hardware timer. `n` selects the timer (0, 1, or 2).

| Prescaler | Divisor | Period per tick (16 MHz) |
|---|---|---|
| 1 | /1 | 62.5 ns |
| 8 | /8 | 500 ns |
| 64 | /64 | 4 µs |
| 256 | /256 | 16 µs |
| 1024 | /1024 | 64 µs |

### Methods

| Method | Description |
|---|---|
| `start()` | Enable the timer |
| `stop()` | Disable the timer (prescaler = 0) |
| `clear()` | Reset TCNT to 0 |
| `set_compare(val: uint8)` | Set OCR register + enable CTC mode |

---

## Examples

### Overflow interrupt (periodic task)

```python
from pymcu.hal.timer import Timer
from pymcu.types import uint8, ptr

TIMSK0: ptr[uint8] = ptr(0x6E)

tick: uint8 = 0

@interrupt(0x0020)    # Timer0 OVF vector (ATmega328P)
def on_timer0_ovf():
    global tick
    tick += 1

def main():
    t = Timer(0, 64)
    TIMSK0[1] = 1     # enable TOIE0 (Timer0 Overflow Interrupt)
    t.start()
    # sei emitted automatically because @interrupt handler is registered
    while True:
        pass
```

### CTC mode (precise frequency)

```python
from pymcu.hal.timer import Timer
from pymcu.types import uint8, ptr

TIMSK1: ptr[uint8] = ptr(0x6F)

@interrupt(0x0018)    # Timer1 COMPA vector
def on_compare():
    pass              # fires at exactly 1 kHz (16 MHz / 64 / 250)

def main():
    t = Timer(1, 64)
    t.set_compare(250)        # OCR1A = 250; CTC mode
    TIMSK1[1] = 1             # enable OCIE1A
    t.start()
    while True:
        pass
```

---

## Timer0 register map (ATmega328P)

| Register | Address | Description |
|---|---|---|
| `TCCR0A` | `0x44` | Waveform generation mode |
| `TCCR0B` | `0x45` | Clock select (prescaler) |
| `TCNT0` | `0x46` | Counter value |
| `OCR0A` | `0x47` | Output Compare A |
| `OCR0B` | `0x48` | Output Compare B |
| `TIMSK0` | `0x6E` | Interrupt mask (TOIE0, OCIE0A, OCIE0B) |
| `TIFR0` | `0x35` | Interrupt flag register |
