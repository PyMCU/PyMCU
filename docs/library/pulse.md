# Pulses — `pymcu.hal.pulse`

```python
from pymcu.hal.pulse import PulseCapture, PulseTrain
```

Two halves of one question: how long were the pulses that arrived, and send these pulses.
Durations are **microseconds** on every architecture, because that is the unit a protocol is
written in. What times the edges is the chip's business and never reaches a caller.

---

## class `PulseCapture`

### `PulseCapture(pin, maxlen=2, idle_state=0)`

Starts recording the length of every pulse arriving on `pin`.

On the ATmega48/88/168/328 family, Timer1 runs free in normal mode at prescaler 8 and a
pin-change interrupt on the pin timestamps every edge; a pulse is the difference between two
timestamps. Timer1's own input capture unit would be the obvious choice and is not used: ICP1
is PB0 and nothing else, and a sensor is wired where the board has room. A pin-change
interrupt costs a few cycles of latency, works on all 23 pins, and the latency is common to
both edges so it cancels in the difference.

Prescaler 8 is 0.5 µs per tick at 16 MHz, and the 16-bit counter still spans 32.7 ms: fine
enough for a DHT's 26 µs and 70 µs bits, and long enough for an infrared leader.

`maxlen` is how many pulses to hold. The buffer is a fixed array of **128** allocated at
compile time, so a larger `maxlen` is refused where the `PulseCapture` is written rather than
quietly given less. A pulse that arrives with the buffer full is dropped.

`idle_state` is the level the line sits at between pulses, and it decides which edge starts
the first pulse. This capture decides that from the line itself: the first edge after a clear
only sets the reference, so the first **stored** interval is the one that follows the line
leaving rest, whichever level rest is. The argument is accepted and needs no register.

**One per program.** The buffer and the edge timestamp are module state; a second
`PulseCapture` would share them.

### Methods

| Method | Return type | Description |
|---|---|---|
| `count()` | `uint16` | How many pulses are waiting |
| `maxlen()` | `uint16` | How many this capture will hold |
| `capacity()` | `uint16` | The buffer's fixed size, 128 |
| `get(i)` | `uint16` | The i-th oldest pulse, left in place; out of range reads 0 |
| `popleft()` | `uint16` | The oldest pulse, removed |
| `clear()` | — | Throw everything away; the next pulse starts from the next edge |
| `pause()` / `resume()` | — | Stop and start recording. Pulses that arrive while paused are lost |
| `paused()` | `uint8` | 1 while stopped |

---

## class `PulseTrain`

### `PulseTrain(pin, freq=38000, duty_u16=32768)`

A carrier on `pin`, off until `send()` gates it on.

On the ATmega family the carrier is Timer2 in fast PWM **mode 7**, where OCR2A is the period,
and it comes out of OC2B, which is **PD3** (D3 on an Arduino board) and nothing else. Mode 7
is the only mode on this part that reaches an arbitrary carrier frequency: the fixed-TOP modes
give 62500, 7812, 1953, 976, 488, 244 and 61 Hz and nothing between, and 38 kHz is not one of
them. OC2A on PB3 cannot be used, because mode 7 spends OCR2A on the period and leaves PB3 no
compare value.

38 kHz at 16 MHz comes out as 52 counts, which is 38 462 Hz: 1.2 % high, well inside the
±5 % any infrared receiver's band-pass allows. A frequency outside about 7.8 kHz to 1 MHz is
refused where the train is written, with the reachable range named.

The two halves use different timers on purpose, so a program can read on one pin and send on
another at the same time.

### Methods

| Method | Description |
|---|---|
| `send(pulses, n)` | Walk `n` durations from `pulses`, carrier on for the first, off for the second, and so on |
| `carrier_on()` / `carrier_off()` | Gate the carrier by hand |
| `deinit()` | Stop the carrier and release the pin |

Timing accuracy: the gaps are measured against Timer1, not counted out in delay calls.
Measured in avr8sharp at 16 MHz, watching the carrier gate itself: 560 µs asked holds the
carrier for 561.50 µs and 1690 µs for 1692.00 µs, within 0.3 %. Stacking `delay_us()` calls
instead put those at 567.88 and 1698.38 — 7 % and 5 % long — because the loop around them
costs real time.

A wait shorter than the call overhead cannot be shortened further, so anything under about
8 µs takes about 8 µs.

---

## Timer ownership

Both halves claim Timer1's prescaler. A PWM or a servo on PB1/PB2 reprograms it, and a
capture whose clock changed reports lengths wrong by that ratio, silently — so the second
claim is refused where it is written:

```
main.py:7:11: error: CompileError: Timer1 prescaler (PB1 and PB2 share it) is already
claimed by 'pulse timing' ...
```

Move the PWM to PD5/PD6 (Timer0) or PB3/PD3 (Timer2), or drop the pulse work. A `PulseTrain`
likewise claims Timer2's prescaler, because mode 7 leaves no room for a plain PWM there.

---

## Example: reading an infrared NEC frame

```python
from pymcu.hal.pulse import PulseCapture
from pymcu.types import uint8, uint16

pulses = PulseCapture("PD2", maxlen=70)

while pulses.count() < 67:
    pass

# 9000 us mark, 4500 us space, then 32 bits: a 560 us mark and a space that is
# 560 us for a zero and 1690 us for a one. Least significant bit first.
address: uint8 = 0
i: uint8 = 0
while i < 8:
    if pulses.get(3 + 2 * i) > 1000:
        address = address | (1 << i)
    i = i + 1
```

## Example: sending one

```python
from pymcu.hal.pulse import PulseTrain
from pymcu.types import uint16

frame: uint16[4] = [560, 560, 1690, 560]

train = PulseTrain("PD3", freq=38000)
train.send(frame, 4)
```
