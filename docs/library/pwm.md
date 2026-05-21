# PWM — `pymcu.hal.pwm`

```python
from pymcu.hal.pwm import PWM
```

Hardware pulse-width modulation. Wraps the Timer/Counter OC channels on AVR.

---

## class `PWM`

### `PWM(pin: str, duty: uint8)`

Configures hardware PWM on the given pin. `duty` is 8-bit (0 = 0%, 255 = 100%).

### Supported pins (ATmega328P)

| Pin | Arduino | Timer channel | Note |
|---|---|---|---|
| `"PD6"` | D6 | Timer0 OC0A | Fast PWM, 8-bit |
| `"PD5"` | D5 | Timer0 OC0B | Fast PWM, 8-bit |
| `"PB1"` | D9 | Timer1 OC1A | Fast PWM, 8-bit |
| `"PB2"` | D10 | Timer1 OC1B | Fast PWM, 8-bit |
| `"PB3"` | D11 | Timer2 OC2A | Fast PWM, 8-bit |
| `"PD3"` | D3 | Timer2 OC2B | Fast PWM, 8-bit |

### Methods

| Method | Description |
|---|---|
| `start()` | Enable PWM output |
| `stop()` | Disable PWM output |
| `set_duty(duty: uint8)` | Update duty cycle while running |

---

## Example

### LED fade

```python
from pymcu.hal.pwm import PWM
from pymcu.time import delay_ms
from pymcu.types import uint8

def main():
    pwm = PWM("PD6", duty=0)    # Timer0 OC0A (D6)
    pwm.start()

    duty: uint8 = 0
    while True:
        pwm.set_duty(duty)
        duty += 5
        delay_ms(20)
```

### Servo control (50 Hz)

```python
from pymcu.hal.pwm import PWM
from pymcu.types import uint8

# Timer1 OC1A gives 16-bit resolution; set OCR1A directly for precision
from pymcu.types import ptr, uint16
OCR1A: ptr[uint16] = ptr(0x88)

pwm = PWM("PB1", duty=0)
pwm.start()

# 50 Hz servo: 1–2 ms pulse out of 20 ms period
# Prescaler 8, ICR1=40000 (20 ms), OCR1A=2000 (1 ms) to 4000 (2 ms)
OCR1A.value = 3000      # center position
```
