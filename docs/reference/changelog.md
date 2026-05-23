# Changelog

## v0.11 *(unreleased)*

### Standard Library

- **`UART.read_line(buf, max_len)`** — Read until `\n` or `max_len` bytes into a fixed-size `uint8[N]` buffer; returns byte count
- **`DS18B20` driver** (`pymcu.drivers.ds18b20`) — 1-Wire temperature sensor; returns raw 12-bit value in 1/16 °C units
- **`machine.Timer`** (`pymcu.compat.micropython`) — MicroPython-compatible timer IRQ API (`timer.irq(handler, trigger)`)
- **`adc_read_temp_raw()`** (`pymcu.hal.adc`) — ATmega328P internal temperature sensor on ADC channel 8 (no external component)
- **LM35 driver** (`pymcu.drivers.lm35`) — Analog temperature sensor over ADC; returns centidegrees

### Compiler

- **`uint16 >> n → uint8` widening shift fix** — right-shift result no longer widens to `uint16` when the rhs forces a one-byte result; fixes truncation in packed-data routines

### Toolchain

- **Programmer plugin system** — custom programmer backends can be registered via `pymcu.plugins`; no longer requires patching the built-in flash command

---

## Unreleased — v0.2 (alpha)

### Language

- `for i in range(n)` loop with runtime or compile-time bound
- `for x in array` iteration over fixed-size arrays
- `for i, x in enumerate(iterable)` with compile-time index counter
- `match / case` OR patterns (`case 1 | 2:`)
- Single-quoted string literals
- `import X as Y` alias
- `//` floor division operator
- Fixed-size arrays `arr: uint8[N]`, constant-index and variable-index access
- Tuple literals and tuple unpacking `a, b = func()`
- Multi-return functions `def f() -> (uint8, uint8): return (q, r)`
- `@property` / `@name.setter` decorators
- Single-level ZCA class inheritance
- `None` literal (folds to `Constant{-1}`)

### Compiler

- Variable→Constant propagation in optimizer
- Fixed inline parameter scope shadowing in `resolve_binding`
- Inline multi-return result variables use 1-dot names

### Standard Library

- `Pin.pulse_in(state, timeout_us)` for pulse measurement
- `UART.print_byte(value)` for decimal uint8 output
- `DHT11` driver (`pymcu.drivers.dht11`)
- `arduino_uno` board pin definitions (`pymcu.boards.arduino_uno`)

---

## v0.1 — Initial Release

- AVR (ATmega328P) backend
- PIC14/14E/18 backend
- Core language: `if/elif/else`, `while`, `match/case`, `def`, `class`, `return`
- GPIO, UART, ADC, Timer, PWM, SPI, I2C HAL modules
- `@inline`, `@interrupt` decorators
- `ptr[T]` and `const[T]` type system
- `delay_ms` / `delay_us` busy-wait delays
- 31 example projects
- 154 integration tests (AVR8Sharp simulator)
