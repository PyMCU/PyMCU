# watchdog

Enable, feed, and disable the watchdog timer.

## What it does

Enables the watchdog with a 500 ms timeout, "feeds" (resets) it 10 times in a
loop printing `FEED` each time, then disables it and prints `DONE`. If the
watchdog were not fed in time it would reset the MCU — the basis of fault
recovery.

## Hardware

- Arduino Uno / any AVR @ 16 MHz
- Serial terminal at **9600 baud**

## Expected output

```
WDT INIT
FEED        (x10)
...
DONE
```

## Key concepts

- `pymcu.hal.watchdog.Watchdog` — `enable()`, `feed()`, `disable()`

## Build & flash

```bash
cd examples/avr/watchdog
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
