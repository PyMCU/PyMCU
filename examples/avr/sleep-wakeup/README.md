# sleep-wakeup

Enter sleep mode and wake on an external interrupt.

## What it does

Puts the MCU into idle sleep with `sleep_idle()`, using an INT0 button (**PD2**)
as the wake source. The wake ISR sets a `GPIOR0` flag; the main loop wakes,
prints `WAKE`, and repeats five times before printing `DONE`.

## Hardware

- Arduino Uno @ 16 MHz
- Button on **PD2** (INT0), internal pull-up
- Serial terminal at **9600 baud**

## Expected output

```
SLEEP DEMO
SLEEP
WAKE      <- on each button press (x5)
...
DONE
```

## Key concepts

- `pymcu.hal.power.sleep_idle()` low-power sleep
- Interrupt as a wake source

## Build & flash

```bash
cd examples/avr/sleep-wakeup
pymcu build
pymcu flash --port /dev/cu.usbmodemXXXX
```
