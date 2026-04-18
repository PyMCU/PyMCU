# Migrating from CircuitPython

The `pymcu-circuitpython` compat package makes CircuitPython migration nearly transparent for
most firmware. Follow these steps:

## Step 1: Install the compat package

```bash
pip install pymcu-circuitpython
```

Add to `pyproject.toml`:

```toml
[tool.pymcu]
stdlib = ["circuitpython"]
target = "atmega328p"
```

## Step 2: Copy your CircuitPython source

Your `code.py` becomes `src/main.py`.  Top-level code compiles as-is — PyMCU automatically
synthesizes an entry point from top-level executable statements, so no wrapper is required.

If you prefer an explicit entry point, wrapping code in `def main():` continues to work.

## Step 3: Add type annotations

PyMCU requires explicit integer widths. Python's built-in `int` is supported as a shorthand
for `int16` and requires no import:

```python
# CircuitPython
count = 0
val = sensor.value

# PyMCU -- int works without any import
count: int = 0
val: int = sensor.value

# For unsigned or wider types, use pymcu.types:
from pymcu.types import uint8, uint16
count: uint16 = 0
val: uint8 = sensor.value
```

## Step 4: Address unsupported features

### Replace `time.sleep(s)` (float seconds)

```python
# CircuitPython
time.sleep(0.5)

# PyMCU
time.sleep_ms(500)
```

### Replace `float` arithmetic

```python
# CircuitPython
temp_c = raw * 3.3 / 1024 * 100

# PyMCU — use integer fixed-point (multiply first, divide last)
temp_c: uint16 = raw * 330 // 1024
```

### Replace `try / except`

```python
# CircuitPython
try:
    val = sensor.read()
except RuntimeError:
    val = 0

# PyMCU — return error sentinel
val: uint16 = sensor.read()   # returns 0xFFFF on error
if val == 0xFFFF:
    val = 0
```

### Replace `f"..."` format strings

```python
# CircuitPython
print(f"temp={temp}")

# PyMCU
uart.write_str("temp=")
uart.print_byte(temp)
```

### Replace `print()` with UART

```python
# CircuitPython
print("hello")

# PyMCU
uart.println("hello")       # via UART object
# or
uart.write_str("hello\n")   # manual newline
```

## Common patterns

### Blink (CircuitPython)

```python
import board, digitalio, time

led = digitalio.DigitalInOut(board.LED)
led.direction = digitalio.Direction.OUTPUT
while True:
    led.value = True
    time.sleep(0.5)
    led.value = False
    time.sleep(0.5)
```

### Blink (PyMCU + pymcu-circuitpython)

```python
import board, digitalio, time

led = digitalio.DigitalInOut(board.LED)
led.direction = digitalio.Direction.OUTPUT
while True:
    led.value = True
    time.sleep_ms(500)    # only change: float → int ms
    led.value = False
    time.sleep_ms(500)
```

## What stays the same

- `import board, digitalio, busio, analogio` — identical API
- `led.value = True/False` — works
- `led.direction = digitalio.Direction.OUTPUT` — works with `@property`
- `uart.write(bytes)` / `uart.read(n)` — works
- `adc.value` — works (returns `uint16`)
- `Pin.pull` — works
