# PyMCU AVR Examples

Working examples for AVR targets (Arduino Uno / ATmega328P @ 16 MHz). Every
folder is a self-contained PyMCU project with its own `pyproject.toml`, a `src/`
directory, and a `README.md` explaining what it does, the wiring, the expected
output, and how to build and flash it.

## New here? Start with these

1. [`blink`](blink) — toggle an LED, no wiring needed. The "hello world".
2. [`uart-echo`](uart-echo) — read and echo serial bytes.
3. [`uart-command`](uart-command) — a tiny interactive command interpreter.

## Build & flash any example

```bash
cd examples/avr/<example>
pymcu build                                  # -> dist/firmware.hex
pymcu flash --port /dev/cu.usbmodemXXXX      # upload to the board
```

`pymcu build` reads `[tool.pymcu]` from the project's `pyproject.toml`. Set a
default flash port under `[tool.pymcu.flash]` to drop the `--port` flag.

## Examples by category

### GPIO & basics
- [`blink`](blink) — LED blink
- [`multi-pin`](multi-pin) — 6 LEDs + 2 buttons, pattern stepper
- [`button-debounce`](button-debounce) — software-debounced press counter
- [`shift-register`](shift-register) — bit-banged 74HC595 running light

### UART / serial
- [`uart-echo`](uart-echo) — byte echo
- [`uart-str`](uart-str) — string/char output helpers
- [`uart-command`](uart-command) — single-char command interpreter
- [`checksum`](checksum) — XOR checksum accumulator
- [`clamp-filter`](clamp-filter) — multi-arg functions over UART
- [`uart-rx-interrupt`](uart-rx-interrupt) — interrupt-driven RX ring buffer

### Interrupts
- [`pin-irq`](pin-irq) — minimal INT0 falling-edge
- [`interrupt-counter`](interrupt-counter) — INT0 press counter
- [`pcint-counter`](pcint-counter) — pin-change interrupt (PCINT0)
- [`stopwatch`](stopwatch) — three simultaneous ISRs

### Timers & PWM
- [`timer-poll`](timer-poll) — overflow flag polling
- [`timer-interrupt`](timer-interrupt) — overflow interrupt
- [`timer-ctc`](timer-ctc) — CTC compare-match interrupt
- [`pwm-fade`](pwm-fade) — single-channel breathing LED
- [`pwm-multi`](pwm-multi) — three independent PWM channels
- [`soft-pwm`](soft-pwm) — software PWM via timer ISR
- [`servo`](servo) — RC servo sweep
- [`tone-buzzer`](tone-buzzer) — melody on a passive buzzer

### ADC
- [`adc-read`](adc-read) — polled single-channel read
- [`adc-interrupt`](adc-interrupt) — interrupt-driven sampling
- [`random-led`](random-led) — ADC-noise-seeded random blink
- [`sensor-dashboard`](sensor-dashboard) — ADC + min/max/EMA + display modes

### I2C
- [`i2c-scanner`](i2c-scanner) — bus address scanner
- [`i2c-irq`](i2c-irq) — interrupt-driven I2C peripheral
- [`bmp280`](bmp280) — pressure/temperature sensor
- [`ssd1306`](ssd1306) — 128x64 OLED

### SPI
- [`spi-cs`](spi-cs) — hardware SPI, custom CS pin
- [`spi-irq`](spi-irq) — interrupt-driven SPI peripheral
- [`spi-shift-register`](spi-shift-register) — hardware SPI → 74HC595
- [`softspi`](softspi) — bit-banged SPI controller
- [`softspi-peripheral`](softspi-peripheral) — bit-banged SPI peripheral
- [`max7219`](max7219) — 8x8 LED matrix

### Displays & devices
- [`lcd`](lcd) — HD44780 character LCD
- [`neopixel`](neopixel) — WS2812B color cycle
- [`dht-sensor`](dht-sensor) — DHT11 temperature/humidity (custom driver)

### Power & reliability
- [`sleep-wakeup`](sleep-wakeup) — sleep + interrupt wake
- [`watchdog`](watchdog) — watchdog enable/feed/disable
- [`eeprom`](eeprom) — non-volatile read/write

### C interop (FFI)
- [`extern-call`](extern-call) — `@extern` basics
- [`ffi-abi`](ffi-abi) — calling-convention validation
- [`ffi-arduino`](ffi-arduino) — Arduino `map()`/`constrain()` in C
- [`ffi-crc8`](ffi-crc8) — avr-libc CRC-8 (Arduino OneWire)
- [`ffi-dsp`](ffi-dsp) — multi-file C build with DSP helpers

### Language features
- [`enum-state`](enum-state) — compile-time constant folding
- [`inheritance-zca`](inheritance-zca) — zero-cost inheritance + overloading
- [`state-machine`](state-machine) — traffic-light FSM with `@property`
- [`error-handling`](error-handling) — `try`/`except`/`raise`
- [`t-flag-demo`](t-flag-demo) — the low-level T-flag error ABI
- [`rtos-multitask`](rtos-multitask) — preemptive RTOS showcase

### Compatibility layers
- [`uart-echo-cp`](uart-echo-cp) — CircuitPython-style API
- [`uart-echo-mp`](uart-echo-mp) — MicroPython-style API
