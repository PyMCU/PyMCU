# rtos-multitask

A preemptive RTOS with weighted round-robin scheduling — the flagship showcase.

## What it does

Four tasks run under a 1 ms Timer1 systick scheduler. Each task's priority
determines how many of the 8 schedule slots it receives per cycle, so priority
visibly affects how fast each task runs:

| Task | Priority | Slots | Job |
|------|----------|-------|-----|
| `sensor_task`  | HIGH (3)   | 4 (50%)  | reads TCNT0, CRC-8, bit-reverse |
| `ledbar1_task` | NORMAL (2) | 2 (25%)  | VU meter on PORTD (PD2–PD7) |
| `ledbar2_task` | LOW (1)    | 1 (12.5%)| Knight Rider on PORTC (PC0–PC5) |
| `blink_task`   | IDLE (0)   | 1 (12.5%)| heartbeat LED on PB5 |

The full context switch (save/restore all 32 GPRs + SREG) is hand-written AVR
assembly in the `@naked @interrupt` systick ISR, with fake initial stack frames
and stack canaries set up per task.

## Files

```
src/main.py     # task definitions + scheduler bootstrap
src/rtos.py     # kernel: scheduler, context switch, delay_ms, stack frames
src/sensor.py   # CRC-8/MAXIM and bit-reverse helpers
src/ledbar.py   # LED bar drivers (VU meter + Knight Rider)
```

## Hardware

- Arduino Uno @ 16 MHz
- VU meter: 6 LEDs on **PORTD PD2–PD7** (D2–D7)
- Knight Rider: 6 LEDs on **PORTC PC0–PC5** (A0–A5)
- Heartbeat LED on **PB5** (D13)

## Key concepts

- `@naked` / `@interrupt(vector)` and inline `asm()` context switching
- Weighted round-robin scheduling and per-task stacks
- Tick-based `delay_ms()` that respects task priority (wall-clock vs CPU time)

## Build & flash

```bash
cd examples/avr/rtos-multitask
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
