# Migrating from CircuitPython

The `whisnake-circuitpython` compat package makes CircuitPython migration nearly transparent for
most firmware. Follow these steps:

## Step 1: Install the compat package

```bash
pip install whisnake-circuitpython
```

Add to `pyproject.toml`:

```toml
[tool.whip]
stdlib = ["circuitpython"]
target = "atmega328p"
```

## Step 2: Copy your CircuitPython source

Your `code.py` becomes `src/main.py`. The `main()` function is the entry point.

## Step 3: Add type annotations

Whisnake requires explicit integer widths. Add them to every variable declaration:

```python
# CircuitPython
count = 0
val = sensor.value

# Whisnake
count: uint16 = 0
val: uint8 = sensor.value
```

## Step 4: Address unsupported features

### Replace `time.sleep(s)` (float seconds)

```python
# CircuitPython
time.sleep(0.5)

# Whisnake
time.sleep_ms(500)
```

### Replace `float` arithmetic

```python
# CircuitPython
temp_c = raw * 3.3 / 1024 * 100

# Whisnake — use integer fixed-point (multiply first, divide last)
temp_c: uint16 = raw * 330 // 1024
```

### Replace `try / except`

```python
# CircuitPython
try:
    val = sensor.read()
except RuntimeError:
    val = 0

# Whisnake — return error sentinel
val: uint16 = sensor.read()   # returns 0xFFFF on error
if val == 0xFFFF:
    val = 0
```

### Replace `f"..."` format strings

```python
# CircuitPython
print(f"temp={temp}")

# Whisnake
uart.write_str("temp=")
uart.print_byte(temp)
```

### Replace `print()` with UART

```python
# CircuitPython
print("hello")

# Whisnake
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

### Blink (Whisnake + whisnake-circuitpython)

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
