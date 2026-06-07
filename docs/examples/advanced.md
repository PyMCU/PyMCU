# Advanced Examples

These examples demonstrate more complex patterns: interrupt-driven I/O,
finite state machines, power management, and C interop.

---

(adv-state-machine)=
## Traffic-Light State Machine

A UK-style traffic light driven by Timer0 overflows. Demonstrates `match`/`case`
for FSM dispatch, `@property` setters as a zero-overhead hardware abstraction,
and compile-time timer constants.

```python
from pymcu.types import uint8, uint16
from pymcu.hal.gpio import Pin
from pymcu.hal.uart import UART
from pymcu.hal.timer import Timer
from pymcu.chips.atmega328p import TIFR0


class State:
    RED        = 0
    RED_YELLOW = 1
    GREEN      = 2
    YELLOW     = 3

DUR_RED    = 732    # 3 s  (244 overflows/s × 3)
DUR_RY     = 244    # 1 s
DUR_GREEN  = 732    # 3 s
DUR_YELLOW = 244    # 1 s


class TrafficLight:
    def __init__(self, red_pin: str, yellow_pin: str, green_pin: str):
        self._red    = Pin(red_pin,    Pin.OUT)
        self._yellow = Pin(yellow_pin, Pin.OUT)
        self._green  = Pin(green_pin,  Pin.OUT)

    @property
    def state(self) -> uint8:
        return 0

    @state.setter
    def state(self, s: uint8):
        match s:
            case State.RED:
                self._red.high(); self._yellow.low(); self._green.low()
            case State.RED_YELLOW:
                self._red.high(); self._yellow.high(); self._green.low()
            case State.GREEN:
                self._red.low(); self._yellow.low(); self._green.high()
            case _:
                self._red.low(); self._yellow.high(); self._green.low()


def main():
    light = TrafficLight("PB0", "PB1", "PB2")
    uart  = UART(9600)
    timer = Timer(0, 256)

    state: uint8  = State.RED
    ticks: uint16 = 0
    dur:   uint16 = DUR_RED

    light.state = State.RED
    uart.println("RED")

    while True:
        if timer.overflow():
            TIFR0[0] = 1           # clear TOV0 (write-1-to-clear)
            ticks = ticks + 1

            if ticks >= dur:
                ticks = 0

                match state:
                    case State.RED:
                        state = State.RED_YELLOW
                        dur   = DUR_RY
                        light.state = State.RED_YELLOW
                        uart.println("RED+YEL")
                    case State.RED_YELLOW:
                        state = State.GREEN
                        dur   = DUR_GREEN
                        light.state = State.GREEN
                        uart.println("GREEN")
                    case State.GREEN:
                        state = State.YELLOW
                        dur   = DUR_YELLOW
                        light.state = State.YELLOW
                        uart.println("YELLOW")
                    case _:
                        state = State.RED
                        dur   = DUR_RED
                        light.state = State.RED
                        uart.println("RED")
```

**Timer0 at prescaler 256, 16 MHz:**
One overflow = 256 × 256 / 16,000,000 = 4.096 ms → 244 overflows ≈ 1 second.

**Wiring:**

```
D8  (PB0)  ←→  Red    LED + 220 Ω to GND
D9  (PB1)  ←→  Yellow LED + 220 Ω to GND
D10 (PB2)  ←→  Green  LED + 220 Ω to GND
```

---

(adv-sensor-dashboard)=
## Sensor Dashboard

ADC sampling driven by Timer0 overflows, min/max tracking, exponential moving
average, and a button that toggles between verbose and compact output.
Demonstrates GPIOR atomic flags for ISR → main-loop signalling.

```python
from pymcu.types import uint8, uint16
from pymcu.chips.atmega328p import GPIOR0, TIFR0
from pymcu.hal.gpio import Pin
from pymcu.hal.uart import UART
from pymcu.hal.timer import Timer
from pymcu.hal.adc import AnalogPin

def timer0_ovf_isr():
    GPIOR0[0] = 1    # signal: timer tick ready

def int0_isr():
    GPIOR0[1] = 1    # signal: button pressed

def main():
    led  = Pin("PB5", Pin.OUT)
    btn  = Pin("PD2", Pin.IN, pull=Pin.PULL_UP)
    uart = UART(9600)
    adc  = AnalogPin("PC0")

    timer = Timer(0, 256)
    timer.irq(timer0_ovf_isr)
    btn.irq(Pin.IRQ_FALLING, int0_isr)

    GPIOR0[0] = 0
    GPIOR0[1] = 0
    uart.println("SENSOR DASHBOARD")

    raw:     uint8  = 0
    avg:     uint8  = 0
    lo:      uint8  = 255
    hi:      uint8  = 0
    verbose: uint8  = 1
    tick:    uint8  = 0

    while True:
        if GPIOR0[0] == 1:
            GPIOR0[0] = 0
            TIFR0[0] = 1           # clear TOV0
            tick += 1

            if tick == 64:         # sample every ~262 ms
                tick = 0

                raw16: uint16 = adc.read()
                raw = raw16 >> 2   # scale 10-bit to 8-bit

                if raw < lo: lo = raw
                if raw > hi: hi = raw
                avg = (avg + raw) >> 1

                led.toggle()

                if verbose == 1:
                    uart.write_str("R:")
                    uart.write_hex(raw)
                    uart.write_str(" A:")
                    uart.write_hex(avg)
                    uart.write_str(" L:")
                    uart.write_hex(lo)
                    uart.write_str(" H:")
                    uart.write_hex(hi)
                    uart.write('\n')
                else:
                    uart.write_hex(avg)
                    uart.write('\n')

        if GPIOR0[1] == 1:
            GPIOR0[1] = 0
            if verbose == 1:
                verbose = 0
                uart.println("MODE:COMPACT")
            else:
                verbose = 1
                uart.println("MODE:VERBOSE")
```

**UART output (verbose mode):** `R:A3 A:92 L:12 H:FF` (raw, avg, min, max as hex).

**GPIOR registers** (0x1E–0x20, in the I/O space) are single-cycle read/write on AVR
and do not need atomic access for single-bit operations — ideal for ISR → main-loop
flags without disabling interrupts.

**Wiring:**

```
A0 (PC0)   ←→  potentiometer or sensor (0–5V analog)
D2 (PD2)   ←→  button to GND (internal pull-up active)
D13 (PB5)  ←→  built-in LED
```

---

(adv-sleep-wakeup)=
## Sleep / Wake on Interrupt

Put the MCU to sleep (IDLE mode) and wake it on a button press (INT0 falling edge).
Repeats 5 times then halts.

```python
from pymcu.types import uint8
from pymcu.chips.atmega328p import GPIOR0
from pymcu.hal.uart import UART
from pymcu.hal.gpio import Pin
from pymcu.hal.power import sleep_idle

def int0_isr():
    GPIOR0[0] = 1    # set wakeup flag

def main():
    uart = UART(9600)
    btn  = Pin("PD2", Pin.IN, pull=Pin.PULL_UP)

    uart.println("SLEEP DEMO")

    GPIOR0[0] = 0
    btn.irq(Pin.IRQ_FALLING, int0_isr)   # configures INT0, enables SEI

    count: uint8 = 0
    while count < 5:
        uart.println("SLEEP")
        sleep_idle()        # CPU halts here until INT0 fires
        if GPIOR0[0] == 1:
            GPIOR0[0] = 0
            count += 1
            uart.println("WAKE")

    uart.println("DONE")

    while True:
        pass
```

**Current consumption in IDLE mode:** ~1 mA at 5V / 16 MHz (peripherals still
active). Use `sleep_power_down()` for deeper sleep (~1 µA), but note that only
asynchronous interrupts (INT0/INT1, TWI address match, watchdog) can wake the MCU
from power-down.

**Wiring:**

```
D2 (PD2)  ←→  button to GND
```

---

(adv-extern-call)=
## C FFI — Calling C Functions from Python

Declare external C functions with `@extern` and link them at firmware build time.
Useful for performance-critical routines (CRC, DSP, crypto) written in C.

```python
from pymcu.types import uint8, inline
from pymcu.hal.uart import UART
from pymcu.ffi import extern

# Stub bodies are ignored by the compiler — only the signature matters.
@extern("c_mul8")
def c_mul8(a: uint8, b: uint8) -> uint8:
    pass

@extern("c_add_saturate")
def c_add_saturate(a: uint8, b: uint8) -> uint8:
    pass

@inline
def print_hex(uart: UART, prefix: uint8, val: uint8):
    uart.write(prefix)
    uart.write(':')
    uart.write_hex(val)
    uart.write('\n')

def main():
    uart = UART(9600)
    print("EXTERN")

    m: uint8 = c_mul8(3, 10)          # 30 = 0x1E
    print_hex(uart, 'M', m)

    s: uint8 = c_add_saturate(200, 100)   # 255 (saturated) = 0xFF
    print_hex(uart, 'S', s)

    a: uint8 = c_add_saturate(4, 6)       # 10 = 0x0A
    print_hex(uart, 'A', a)

    print("OK")
    while True:
        pass
```

The matching C source (`c_src/math_helper.c`):

```c
#include <stdint.h>

uint8_t c_mul8(uint8_t a, uint8_t b) {
    return a * b;
}

uint8_t c_add_saturate(uint8_t a, uint8_t b) {
    uint16_t r = (uint16_t)a + b;
    return r > 255 ? 255 : (uint8_t)r;
}
```

Register `c_src/math_helper.c` in `pyproject.toml`:

```toml
[tool.pymcu.ffi]
sources = ["c_src/math_helper.c"]
```

**AVR calling convention:** arguments are passed right-to-left in register pairs
starting at R25:R24 (arg0), R23:R22 (arg1), R21:R20 (arg2). `uint8` values use only
the low register of each pair (R24, R22, R20). Return value is in R24 (R25:R24 for 16-bit).
