# Changelog — pymcu-compiler / pymcu-stdlib

## Unreleased

### Correctness (silent-miscompile class)
- The counter of `for i in range(...)` is sized from its bounds instead of being an
  unconditional uint8: `range(300)` ran 44 times, `range(0, 256)` never ran,
  `range(200, -1, -1)` never ran, a `uint16` stop variable and a `uint16` annotation on
  the loop variable were ignored, and `range(0, 250, 30)` wrapped its counter (#284).
- The range loop variable holds the last value visited after the loop, as in Python:
  it read 0 after an unrolled constant range and `stop` after a runtime one (#285).
- A signed runtime step (`range(10, 0, step)` with `step: int8 = -2`) counts down instead
  of exiting before the first iteration (#286).
- List comprehensions over `range(start, stop, step)` honour the step (#287).

### Language
- `x in range(a, b[, s])`, `reversed(range(...))` and `enumerate(range(...))` over runtime
  bounds are supported; `range()` as a value is refused with a message that names the
  spellings that work (#288).

## 0.1.0a10 — 2026-08-18

The hardware-validation release. Everything below came out of a sustained
bug-hunting campaign on a real Arduino Uno with a logic analyzer, plus a
sweep of the official MicroPython quickref and CircuitPython Essentials
examples (63 projects; 53 compile, the rest fail on purpose with a clear
diagnostic). Suites at release: 517 unit, 508 driver, 1549 AVR integration.

### Correctness (silent-miscompile class)
- Copy propagation no longer forwards through same-width float<->int casts:
  `uint32(float_var)` produced raw float bits (16464 for 3.25 on a real Uno).
- An unannotated module global widens to its call-result type instead of
  wrapping at a uint8 store (`f0 = pwm.freq()` printed 232 for 1000).
- A user global no longer shadows a same-named function parameter -- neither
  of an `@inline` (`data = 5` broke `uart.write('hello')`) nor of a plain def
  (`start_low_ms = 250` silently drove the DHT driver's start pulse for
  250 ms instead of 18).
- `raise CompileError` inside an `@inline` body aborts compilation even when
  the call site sits under runtime control flow (a swallowed raise had let
  `readline()` compile to an unbound temp written to UDR0).
- `print(bytearray)` streamed the array variable as a scalar and printed
  garbage; it now prints the CPython `bytearray(b'...')` repr.
- `millis()`/`ticks_ms`/`monotonic` count real milliseconds (Arduino-style
  fractional correction; a 1 s blink measured 1024 ms before), and
  `micros()` no longer jumps backward across a Timer0 overflow.
- The second PWM channel of a timer no longer disconnects the first
  (shared TCCRxA COM bits are OR-ed in).
- `uart_write_float` prints two rounded decimals with the trailing zero
  trimmed (3.25 printed 3.2 before; 0.05 printed 0.0).

### Language surface
- Slice assignment dispatches through `__setitem__` with a bytes/list
  literal source: `microcontroller.nvm[0:4] = b'\xcc\x10\xca\xfe'`.
- `for b in buf[0:n]` accepts runtime bounds (rewritten to a range loop).
- `str.join` in assignment: compile-time strings fold to a constant;
  `''.join([chr(b) for b in buf])` builds a runtime string.
- `const` parameters accept compile-time float constants (`Timer(freq=2.5)`).
- Exception catch-all forms, user exception classes, bare `except`;
  `__bool__`/`__len__` truthiness; `__call__`; n-ary `min`/`max`;
  `dict.get` on literal dicts.
- A nested constructor argument types as its class in overload resolution
  (`ADC(Pin(14))` picks the Pin overload); overload resolution matches
  parameter types, not declaration order.

### Guardrails (was silent, now a located error)
- A `const[...]` parameter rejects runtime-varying arguments (a loop
  variable passed to `Pin()` silently drove a fixed pin before).
- An image larger than the chip's flash, and static SRAM beyond the chip's
  RAM, are build errors with the part's real numbers.
- Runtime tuples, filtered comprehensions, list parameters, instance
  interpolation and iterator-protocol loops all get specific diagnostics
  instead of misbehaving quietly.

### Requires
- pymcu-avr >= 0.1.0a9 (paired codegen fixes: float conversions and
  comparisons, wide constants, linker MEMORY regions).
