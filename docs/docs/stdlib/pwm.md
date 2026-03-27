# PWM — `pymcu.hal.pwm`

```python
from pymcu.hal.pwm import PWM
```

## `PWM(pin: str, duty: uint8)`

Configures hardware PWM on the given pin. On ATmega328P, the supported PWM pins and their
associated Timer/Counter channels are:

| Pin | Arduino | Timer channel | Note |
|---|---|---|---|
| `"PD6"` | D6 | Timer0 OC0A | Fast PWM, 8-bit |
| `"PD5"` | D5 | Timer0 OC0B | Fast PWM, 8-bit |
| `"PB1"` | D9 | Timer1 OC1A | Fast PWM, 8-bit |
| `"PB2"` | D10 | Timer1 OC1B | Fast PWM, 8-bit |
| `"PB3"` | D11 | Timer2 OC2A | Fast PWM, 8-bit |
| `"PD3"` | D3 | Timer2 OC2B | Fast PWM, 8-bit |

`duty` is an 8-bit value (0 = 0%, 255 = 100%).

### Methods

| Method | Description |
|---|---|
| `start()` | Enable PWM output |
| `stop()` | Disable PWM output |
| `set_duty(duty: uint8)` | Update duty cycle while running |

## Example

```python
from pymcu.hal.pwm import PWM
from pymcu.time import delay_ms
from pymcu.types import uint8

def main():
    pwm = PWM("PD6", duty=0)    # OC0A
    pwm.start()

    duty: uint8 = 0
    while True:
        pwm.set_duty(duty)
        duty += 5
        delay_ms(20)
```
