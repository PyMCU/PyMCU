# Migrating from MicroPython

The `whisnake-micropython` compat package provides `machine`, `utime`, and `micropython` module
names so most MicroPython firmware targeting Arduino Uno ports with minimal edits.

## Step 1: Install the compat package

```bash
pip install whisnake-micropython
```

Add to `pyproject.toml`:

```toml
[tool.whip]
stdlib = ["micropython"]
target = "atmega328p"
```

## Step 2: Copy your MicroPython source

Your `main.py` stays as `src/main.py`. The `main()` function is the entry point (unlike
MicroPython's top-level execution; wrap your top-level code in `def main():`).

## Step 3: Add type annotations

```python
# MicroPython
count = 0
data = bytearray(8)

# Whisnake
count: uint16 = 0
data: uint8[8] = [0, 0, 0, 0, 0, 0, 0, 0]
```

## Step 4: Replace unsupported features

### Replace `Pin.irq()` callbacks

```python
# MicroPython
btn.irq(trigger=Pin.IRQ_FALLING, handler=on_btn)

# Whisnake — use @interrupt decorator
@interrupt(0x0002)   # INT0 vector
def on_btn():
    global count
    count += 1

# Then configure trigger in your init:
btn = Pin(2, Pin.IN)
btn.irq(Pin.IRQ_FALLING)   # sets up EICRA/EIMSK; handler is @interrupt above
```

### Replace `Timer` callbacks

```python
# MicroPython
tim = Timer(0, freq=1)
tim.callback(on_timer)

# Whisnake
@interrupt(0x0020)   # Timer0 OVF
def on_timer():
    global tick
    tick += 1

# In main():
TIMSK0[1] = 1    # enable TOIE0
t = Timer0(prescaler=64)
t.start()
```

### Replace `machine.mem8`

```python
# MicroPython
machine.mem8[0x25] = 0xFF

# Whisnake
from whisnake.types import ptr, uint8
PORTB: ptr[uint8] = ptr(0x25)
PORTB.value = 0xFF
```

### Replace `micropython.const`

```python
# MicroPython
BAUD = micropython.const(9600)

# Whisnake — micropython.const() is a no-op; just use the value or const[T]
BAUD: const[uint16] = 9600
```

### Replace `float`

```python
# MicroPython
temp = adc.read() * 3.3 / 4096 * 100

# Whisnake
temp: uint16 = adc.read() * 330 // 4096
```

## Common patterns

### Blink (MicroPython)

```python
from machine import Pin
from utime import sleep_ms

led = Pin(13, Pin.OUT)
while True:
    led.value(1)
    sleep_ms(500)
    led.value(0)
    sleep_ms(500)
```

### Blink (Whisnake + whisnake-micropython)

```python
from machine import Pin       # identical
from utime import sleep_ms    # identical

def main():
    led = Pin(13, Pin.OUT)    # integer pin numbers work
    while True:
        led.value(1)
        sleep_ms(500)
        led.value(0)
        sleep_ms(500)
```

The only required change is wrapping in `def main():`.

## What stays the same

- `from machine import Pin, UART, ADC, SPI, I2C` — identical API
- `Pin(13, Pin.OUT)` — integer Arduino pin numbers supported
- `uart.write(b"...")` / `uart.read(n)` — works
- `adc.read_u16()` — returns scaled 16-bit value
- `from utime import sleep_ms, sleep_us` — identical
- `micropython.const(N)` — treated as integer literal `N`
