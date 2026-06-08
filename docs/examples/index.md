# Examples

Annotated firmware examples showing real-world PyMCU patterns.

Unless otherwise noted, examples target **ATmega328P / Arduino Uno** and ship with a
cycle-accurate integration test (AVR8Sharp simulator) so you can run them without
hardware. {doc}`Raspberry Pi Pico (RP2040) <rp2040>` examples run on the alpha ARM
backend and are validated with the RP2040Sharp emulator.

```{toctree}
:maxdepth: 1

micropython
circuitpython
hal
advanced
rp2040
```

---

## MicroPython compatibility layer

Use these examples if you are porting code from MicroPython or writing new
firmware that should remain runnable on a real MicroPython board.

| Example | Topics |
|---|---|
| {ref}`mp-blink` | `machine.Pin`, `utime.sleep_ms` |
| {ref}`mp-uart-echo` | `machine.UART`, `machine.Pin` |
| {ref}`mp-adc-read` | `machine.ADC`, `machine.UART` |
| {ref}`mp-dht-sensor` | `machine.Pin`, local DHT11 driver |
| {ref}`mp-ticks-ms` | `utime.ticks_ms`, `utime.ticks_diff` |
| {ref}`mp-signal-led` | `machine.Signal`, active-low logic |
| {ref}`mp-pwm-fade` | `machine.PWM` (hardware) + soft PWM |

---

## CircuitPython compatibility layer

Use these examples if you are porting Adafruit / CircuitPython code or writing
firmware that should remain runnable on a real CircuitPython board.

| Example | Topics |
|---|---|
| {ref}`cp-blink` | `board`, `digitalio`, `time` |
| {ref}`cp-uart-echo` | `busio.UART`, `digitalio` |
| {ref}`cp-button-led` | `digitalio` INPUT + OUTPUT, pull-up |
| {ref}`cp-dht-sensor` | `busio`, `digitalio`, local DHT11 driver |
| {ref}`cp-morse-blinker` | `digitalio`, `time`, `@inline` |
| {ref}`cp-traffic-light` | `digitalio`, `time`, state machine |
| {ref}`cp-adc-pwm` | `analogio.AnalogIn`, `pwmio.PWMOut` |

---

## PyMCU HAL (native)

Direct use of the PyMCU hardware abstraction layer — maximum control,
zero overhead.

| Example | Topics |
|---|---|
| {ref}`hal-blink` | `Pin`, `delay_ms` |
| {ref}`hal-button-debounce` | `Pin`, edge detection, `uint16` |
| {ref}`hal-eeprom` | `EEPROM.read` / `EEPROM.write` |
| {ref}`hal-watchdog` | `Watchdog.enable` / `feed` / `disable` |
| {ref}`hal-i2c-scanner` | `I2C.ping`, hex output |
| {ref}`hal-ssd1306` | `SSD1306` driver, I2C |
| {ref}`hal-neopixel` | `NeoPixel` driver, interrupt-safe |

---

## Advanced patterns

| Example | Topics |
|---|---|
| {ref}`adv-state-machine` | Timer, `match`/`case` FSM, `@property` |
| {ref}`adv-sensor-dashboard` | ADC, Timer ISR, GPIOR flags, UART |
| {ref}`adv-sleep-wakeup` | `sleep_idle`, `Pin.irq`, power management |
| {ref}`adv-extern-call` | `@extern`, C FFI, `avr-gcc` linking |

---

## Raspberry Pi Pico (RP2040) — alpha

Firmware for the **Raspberry Pi Pico** via the alpha {doc}`ARM backend
<../getting-started/installation>` (`pip install pymcu-arm`) — GPIO + UART0 on core 0.
All three API styles (native HAL, MicroPython, CircuitPython) are shown in
{doc}`rp2040`.
