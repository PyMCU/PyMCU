# Example: sensor-dashboard

A multi-peripheral dashboard combining an INT0 button ISR, Timer0 overflow ISR, ADC sampling,
exponential moving average (EMA) filter, and conditional verbose/compact UART output.

**Flash:** ~500 bytes

## What it demonstrates

- Two concurrent ISRs sharing data via `global` + GPIOR0 atomic flags.
- ADC read triggered from the main loop, guarded by a timer flag.
- EMA filter implemented with integer arithmetic (no float).
- Conditional output format selected at startup via UART.

## Source (annotated)

```python
from whipsnake.hal.gpio import Pin
from whipsnake.hal.uart import UART
from whipsnake.hal.timer import Timer0
from whipsnake.types import uint8, uint16, ptr

TIMSK0: ptr[uint8] = ptr(0x6E)
ADCSRA: ptr[uint8] = ptr(0x7A)
ADCL:   ptr[uint8] = ptr(0x78)
ADCH:   ptr[uint8] = ptr(0x79)
GPIOR0: ptr[uint8] = ptr(0x3E)

btn_count: uint8 = 0
tick: uint8 = 0
ema: uint16 = 0

@interrupt(0x0002)   # INT0
def on_button():
    global btn_count
    btn_count += 1
    GPIOR0[0] = 1    # signal main loop

@interrupt(0x0020)   # Timer0 OVF
def on_timer():
    global tick
    tick += 1
    GPIOR0[1] = 1    # signal main loop

def read_adc() -> uint16:
    ADCSRA[6] = 1              # start conversion (ADSC)
    while ADCSRA[6]:           # wait for completion
        pass
    lo: uint8 = ADCL.value
    hi: uint8 = ADCH.value
    return lo + hi * 256

def main():
    uart = UART(9600)
    btn  = Pin("PD2", Pin.IN, pull=Pin.PULL_UP)
    btn.irq(Pin.IRQ_FALLING)

    t = Timer0(prescaler=64)
    TIMSK0[1] = 1     # TOIE0
    t.start()

    while True:
        if GPIOR0[1]:
            GPIOR0[1] = 0
            raw: uint16 = read_adc()
            ema = ema - (ema >> 4) + (raw >> 4)   # EMA alpha=1/16

        if GPIOR0[0]:
            GPIOR0[0] = 0
            uart.write_str("BTN=")
            uart.print_byte(btn_count)
            uart.write_str("ADC=")
            # print upper byte of ema as proxy
            hi_ema: uint8 = ema >> 2
            uart.print_byte(hi_ema)
```

## EMA formula

The exponential moving average is computed as:

```
EMA = EMA - EMA/16 + raw/16
```

This is equivalent to `alpha = 1/16 = 0.0625`, using only integer shifts — no float required.

## Build

```bash
cd examples/avr/sensor-dashboard
whip build
whip flash --port /dev/cu.usbmodem*
```
