# GPIO — `whipsnake.hal.gpio`

```python
from whipsnake.hal.gpio import Pin
```

## `Pin(name, mode, pull=-1, value=-1)`

Creates a GPIO pin. All parameters except `name` and `mode` are optional.

| Parameter | Type | Description |
|---|---|---|
| `name` | `str` | Port/pin name: `"PB5"`, `"PD2"`, `"PC0"`, etc. |
| `mode` | `uint8` | `Pin.OUT` or `Pin.IN` |
| `pull` | `uint8` | `Pin.PULL_UP` or `Pin.PULL_DOWN` (input only) |
| `value` | `uint8` | Initial output value (output only) |

### Constants

| Constant | Value | Description |
|---|---|---|
| `Pin.OUT` | 0 | Output mode |
| `Pin.IN` | 1 | Input mode |
| `Pin.OPEN_DRAIN` | 2 | Open-drain output |
| `Pin.PULL_UP` | 1 | Enable pull-up resistor |
| `Pin.PULL_DOWN` | 2 | Enable pull-down resistor |
| `Pin.IRQ_FALLING` | 1 | Interrupt on falling edge |
| `Pin.IRQ_RISING` | 2 | Interrupt on rising edge |
| `Pin.IRQ_LOW_LEVEL` | 4 | Interrupt on low level |
| `Pin.IRQ_HIGH_LEVEL` | 8 | Interrupt on high level |

### Methods

| Method | Description |
|---|---|
| `high()` | Drive pin high |
| `low()` | Drive pin low |
| `on()` | Alias for `high()` |
| `off()` | Alias for `low()` |
| `toggle()` | XOR pin with 1 |
| `value(x=-1)` | Read pin if `x==-1`, else write `x` |
| `mode(m=-1)` | Read current mode if `m==-1`, else set mode |
| `pull(p)` | Configure pull resistor |
| `irq(trigger)` | Configure interrupt trigger |
| `pulse_in(state, timeout_us=1000)` | Measure pulse width in microseconds |

## Examples

```python
from whipsnake.hal.gpio import Pin
from whipsnake.types import uint8

# Output pin
led = Pin("PB5", Pin.OUT)
led.high()
led.low()
led.toggle()

# Input pin with pull-up
btn = Pin("PD2", Pin.IN, pull=Pin.PULL_UP)
v: uint8 = btn.value()

# Interrupt-driven input (AVR INT0)
btn2 = Pin("PD2", Pin.IN)
btn2.irq(Pin.IRQ_FALLING)   # setup trigger; use @interrupt(0x0002) for handler

# Pulse measurement (e.g. DHT11, HC-SR04)
echo = Pin("PD4", Pin.IN)
width: uint16 = echo.pulse_in(1, timeout_us=10000)
```

## Using board pin names (Arduino Uno)

```python
from pymcu.boards.arduino_uno import D13, D2, A0, LED_BUILTIN

led = Pin(LED_BUILTIN, Pin.OUT)    # same as Pin("PB5", Pin.OUT)
btn = Pin(D2, Pin.IN)              # same as Pin("PD2", Pin.IN)
```
