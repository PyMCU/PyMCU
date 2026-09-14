# PWM — `pymcu.hal.pwm`

```python
from pymcu.hal.pwm import PWM
```

Hardware pulse-width modulation. Wraps the Timer/Counter OC channels on AVR.

---

## class `PWM`

### `PWM(pin: str, duty: uint8, freq: uint16 = 0, invert: const[uint8] = 0)`

Configures hardware PWM on the given pin. `duty` is 8-bit (0 = 0%, 255 = 100%). `freq` is
optional; `0` leaves the timer at its default prescaler.

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
| `start()` | Put the compare output back on the pin (a duty of 0 stays off) |
| `stop()` | Take the compare output off the pin and drive it low; the timer keeps running for its other channel and for the time base |
| `deinit()` | `stop()`, then the pin back to an input without pull-up |
| `set_duty(duty: uint8)` | Update the 8-bit duty while running (0 off, 255 fully on, else high for duty + 1 of 256 counts) |
| `set_duty_u16(duty: uint16)` | Update the 16-bit duty, 0..65535 = 0..100 %, exact to the channel's resolution: 32768 is 50.0 %, 65535 fully on, below half a count is off |
| `set_freq(freq: uint16)` | Select the prescaler closest to `freq` |

### Two duty entries, one exact

`PWM(pin, duty)` and `set_duty()` take the 8-bit duty the HAL always had. `PWM(pin,
duty_u16=...)` and `set_duty_u16()` take the 16-bit one every architecture's HAL shares
(0..65535 = 0..100 %, what CircuitPython's `duty_cycle` and MicroPython's `duty_u16` mean),
and each chip module resolves it to its own compare register. On the AVR 8-bit channels the
value becomes round(duty * 256 / 65535) counts high and the compare register holds one less,
because fast PWM is high for OCR + 1 counts. Measured before this existed: every 16-bit duty
came out 1/256 above what was asked, 50.4 % for 32768 on an Arduino Uno. The compat layers
use only the 16-bit entry and know nothing about the resolution behind it.

### Timer0 is also the time base

On the ATmega parts PD5 and PD6 (Arduino D5 and D6) are the two channels of Timer0, and
Timer0's overflow is what `millis()`, `ticks_ms()`, `time.monotonic()` and asyncio count.
The time base runs it at prescaler 64, so while it is in the program (the build injects
`millis_init()` for those calls, or the sources call it) the only frequency available on
those two pins is the 976 Hz bucket, which is also the default. Any other request there
is refused where it is written:

```
error: CompileError: PWM: PD5/PD6 (Arduino D5/D6) share Timer0 with the millisecond time
base (millis, ticks_ms, monotonic, asyncio), which fixes its prescaler at 64 ...
```

Use PD3/PB3 (D3/D11, Timer2) or PB1/PB2 (D9/D10, Timer1) for that frequency. Without the
time base every bucket stays available on Timer0. Measured before this rule existed: a
5000 Hz PWM on D6 made `monotonic()` run 8.44 times too fast on an Arduino Uno.

### The two channels of one timer share its prescaler

PD5 and PD6 (Timer0), PB1 and PB2 (Timer1), PB3 and PD3 (Timer2) come in pairs, and each
pair runs at one frequency. The `PWM()` that is built second, or a `set_freq()` on a
channel whose sibling is running, used to reprogram the prescaler for both in silence
(measured on an Arduino Uno: 5000 Hz asked on D5 and 100 Hz on D6 left both at 61 Hz).
The HAL now records the prescaler it programs with `claim()` from `pymcu.types`, a
compile-time intrinsic that emits nothing, and the compiler refuses a second channel
asking for another bucket where it is written:

```
error: CompileError: Timer0 prescaler (PD5 and PD6 share it): already 2 for PD5 at line 10,
and PD6 asks for 5. The two channels of one timer run at one frequency. Timer0 codes:
1 = 62500 Hz, 2 = 7812 Hz, 3 = 976 Hz, 4 = 244 Hz, 5 = 61 Hz. Ask both for the same
frequency, or move one to PB1/PB2 (Timer1) or PB3/PD3 (Timer2)
```

Two channels asking the same bucket share it; a channel alone on its timer may retune it
with `set_freq()`. A run-time frequency cannot be checked at compile time and goes through.

`claim(key, value, owner="", hint="")` is available to any HAL: the first claim on a key
records the value and its owner, the same value from another owner is shared, another
value is refused unless the only owner so far is the claimant. Claims are visited in
lowering order, hold for the whole program, and are ignored inside a branch the compiler
cannot decide.

### Inverting output

`invert=1` selects the inverting compare output mode (AVR: COMxn1:COMxn0 = 11). The pin is
set on compare match and cleared at BOTTOM, so `duty` counts the LOW time: `duty=64` is 75 %
high. `set_duty(0)` still disconnects the compare output and drives the pin low; a non-zero
duty reconnects it inverting. The argument is a compile-time constant and costs nothing when
it is 0; `machine.PWM(pin, invert=1)` lowers to it.

### Reachable frequencies

An AVR timer does not run at an arbitrary frequency — the prescaler picks from a handful of
buckets. At 16 MHz, Timer0 and Timer1 reach 61, 244, 976, 7812 and 62500 Hz; Timer2 also has
/32 and /128, so it adds 488 and 1953 Hz.

`set_freq(freq)` chooses the **nearest** reachable bucket, comparing against the geometric
midpoints between them. Asking for 440 Hz on Timer2 lands on 488 Hz rather than dropping to
244 Hz — which is what keeps `tone()` melodies recognisably in tune. Read back through the
compat layer (`machine.PWM.freq()`) and you get the value you asked for, not the bucket; if
you need the physical frequency, derive it from `__FREQ__` and the prescaler.

### Duty at the extremes

`duty = 255` is 100% because fast PWM with the compare register at MAX holds the output
constantly high. `duty = 0` is not 0% for the same reason in reverse: the output is set at
BOTTOM and cleared on compare match, so a compare register of 0 would leave a
one-prescaled-clock pulse in every 256 -- about 0.4%, enough to keep an LED visibly lit
(ATmega328P section 15.7.3, "Fast PWM Mode").

So `duty = 0` does not write 0 to the compare register. It clears the channel's COM bits in
`TCCRxA`, which returns the pin to normal port operation, and drives it low; the next
non-zero duty writes the compare register and sets the COM bits back. The pin stays an
output throughout. A duty that is a compile-time constant folds to one path or the other
with no runtime branch; a duty computed at run time costs one compare.

### Two channels on one timer

The channels of a single timer (D5+D6 on Timer0, D9+D10 on Timer1, D11+D3 on Timer2) can
run at the same time: configuring the second OR-s its COM bits into `TCCRxA` instead of
overwriting the register, so the first channel keeps its output. They do share the
prescaler, so they cannot have different frequencies.

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
