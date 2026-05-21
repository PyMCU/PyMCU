# GPIO — `pymcu.hal.gpio`

```python
from pymcu.hal.gpio import Pin
```

Digital input/output control. `Pin` is an `@inline` class — it compiles to DDR/PORT register
writes with zero SRAM allocation.

---

## class `Pin`

### `Pin(name, mode, pull=-1, value=-1)`

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
| `value(x=-1)` | Read pin if `x == -1`, else write `x` |
| `mode(m=-1)` | Read current mode if `m == -1`, else set mode |
| `pull(p)` | Configure pull resistor |
| `irq(trigger)` | Configure interrupt trigger hardware |
| `pulse_in(state, timeout_us=1000)` | Measure pulse width in microseconds |

---

## Examples

### Output pin

```python
from pymcu.hal.gpio import Pin

led = Pin("PB5", Pin.OUT)
led.high()
led.low()
led.toggle()
```

### Input pin with pull-up

```python
from pymcu.hal.gpio import Pin
from pymcu.types import uint8

btn = Pin("PD2", Pin.IN, pull=Pin.PULL_UP)
v: uint8 = btn.value()
```

### Interrupt-driven input

```python
from pymcu.hal.gpio import Pin
from pymcu.types import uint8

count: uint8 = 0

@interrupt(0x0002)      # INT0 vector (ATmega328P, pin D2)
def on_press():
    global count
    count += 1

def main():
    btn = Pin("PD2", Pin.IN, pull=Pin.PULL_UP)
    btn.irq(Pin.IRQ_FALLING)    # configure EICRA/EIMSK hardware
    # sei is emitted automatically when @interrupt handler exists
    while True:
        pass
```

### Pulse measurement (e.g. HC-SR04)

```python
from pymcu.hal.gpio import Pin
from pymcu.types import uint16

echo = Pin("PD4", Pin.IN)
width: uint16 = echo.pulse_in(1, timeout_us=30000)
# width is duration in microseconds; 0 on timeout
```

---

## Board pin names

```python
from pymcu.boards.arduino_uno import D13, D2, A0, LED_BUILTIN

led = Pin(LED_BUILTIN, Pin.OUT)    # same as Pin("PB5", Pin.OUT)
btn = Pin(D2, Pin.IN)              # same as Pin("PD2", Pin.IN)
```

---

## Arduino Uno pin mapping

| Integer | Arduino silk | Port | Notes |
|---|---|---|---|
| 0 | D0 / RX | PD0 | USART0 RX |
| 1 | D1 / TX | PD1 | USART0 TX |
| 2 | D2 | PD2 | INT0 |
| 3 | D3 | PD3 | INT1 / OC2B |
| 5 | D5 | PD5 | OC0B (Timer0 PWM) |
| 6 | D6 | PD6 | OC0A (Timer0 PWM) |
| 9 | D9 | PB1 | OC1A (Timer1 PWM) |
| 10 | D10 | PB2 | OC1B (Timer1 PWM) |
| 11 | D11 | PB3 | OC2A / MOSI |
| 13 | D13 / LED | PB5 | Built-in LED / SCK |
| A0–A3 | A0–A3 | PC0–PC3 | ADC0–ADC3 |
| A4 / SDA | A4 | PC4 | I2C SDA |
| A5 / SCL | A5 | PC5 | I2C SCL |
