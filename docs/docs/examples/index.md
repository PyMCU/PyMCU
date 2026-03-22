# Examples Gallery

All examples target the ATmega328P (Arduino Uno) at 16 MHz. Flash sizes are for Release builds
with `-O2`.

## Core Examples

| Example | Flash | Description |
|---|---|---|
| [blink](blink.md) | 124 B | Built-in LED toggle with `delay_ms` |
| [uart-echo](uart-echo.md) | 170 B | Echo bytes received on UART0 |
| `uart-str` | 266 B | Flash string pool, `write_str`, `println` |
| `delay-test` | 258 B | UART timing sentinels, tests `delay_ms` |
| `adc-read` | 226 B | ADC polling, read 10-bit value |
| `pwm-fade` | 122 B | Timer0 CTC PWM fade on OC0A |
| `button-debounce` | 318 B | 16-bit counter, uint16 equality |
| `shift-register` | 206 B | 74HC595 bit-bang, variable shift |
| `bitwise-ops` | 410 B | All bitwise operators, variable shifts |
| `multi-pin` | 360 B | 6 LEDs + 2 buttons, `match/case` |

## Interrupts and Timers

| Example | Flash | Description |
|---|---|---|
| `timer-poll` | 168 B | Timer0 OVF flag polling |
| `interrupt-counter` | 256 B | INT0 ISR + GPIOR0 atomic flag |
| `timer-interrupt` | 272 B | Timer1 OVF ISR (~1 Hz blink) |
| `multi-isr` | 392 B | Timer0_OVF + INT0 with GPIOR0 flags |
| `pcint-counter` | 366 B | PCINT0 ISR, button, decimal counter |
| `timer2-interrupt` | 308 B | Timer2 OVF ISR, prescaler 1024 |
| `stopwatch` | 440 B | Three ISRs (INT0 + INT1 + Timer0) |

## Protocols

| Example | Flash | Description |
|---|---|---|
| `spi-shift-register` | 482 B | HW SPI to 74HC595, 3 modes |
| `i2c-scanner` | 516 B | TWI bus scan 0x01-0x7F |

## Advanced Patterns

| Example | Flash | Description |
|---|---|---|
| `state-machine` | 388 B | Traffic light FSM with Timer0 |
| `uart-command` | 690 B | UART command parser (B/H/L/T/S/?) |
| `callbacks` | 326 B | Function dispatch via `match/case` |
| `matrix-math` | 382 B | `uint8[4]` frame buffer |
| `uint16-math` | 600 B | 16-bit add/sub/compare |
| `checksum` | 190 B | XOR accumulator, `uart.read`, AugAssign |
| `nested-calls` | 314 B | 3-level call chain, nibble-to-hex |
| `soft-pwm` | 424 B | Timer0 software PWM, duty bounce |
| `clamp-filter` | 304 B | `clamp(val,lo,hi)`, nested calls |
| [sensor-dashboard](sensor-dashboard.md) | 500 B | Timer0 + INT0 ISRs, ADC sampling, EMA |

## Language Features

| Example | Flash | Description |
|---|---|---|
| [tuple-ops](tuple-ops.md) | — | Multi-return, tuple unpacking, enumerate |
| [inheritance-zca](inheritance-zca.md) | — | ZCA single-level inheritance |
| `array-ops` | — | Fixed-size arrays, variable index |
| `list-comp` | — | Compile-time list comprehension |
| `stress-math` | — | 8-bit and 16-bit arithmetic stress test |
| `edge-cases` | — | Compiler edge-case coverage |

## Peripherals

| Example | Flash | Description |
|---|---|---|
| `dht-sensor` | 7120 B | DHT11 temperature/humidity, local driver |
| `eeprom-store` | — | Persist calibration bytes across power cycles |
| `watchdog-blink` | — | WDT-enabled blink; auto-reset on hang |
| `sleep-wakeup` | — | Power-down sleep, wake on INT0 (~0.1 µA) |

## CircuitPython compat

| Example | Flash | Description |
|---|---|---|
| `uart-echo-cp` | — | UART echo using `whisnake-circuitpython` |

## MicroPython compat

| Example | Flash | Description |
|---|---|---|
| `blink` (uPy) | 850 B | `from machine import Pin` + `sleep_ms` |
| `uart-echo` (uPy) | 902 B | `from machine import UART` read/write |
| `adc-read` (uPy) | 550 B | `from machine import ADC` + `read_u16()` |
