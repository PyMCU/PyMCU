# Changelog — pymcu-compiler / pymcu-stdlib

## Unreleased

### Peripherals (measured on an Arduino Uno with a scope)
- `PWM.stop()` takes the channel off the pin and drives it low instead of stopping the
  timer, which froze the sibling channel and the time base and left the pin at whatever
  level the compare latch had (5 V half the time after `pwmio.deinit()`); `PWM.deinit()`
  is new and returns the pin to an input, which `pwmio.PWMOut.deinit()` now calls (#296).
- A PWM on PD5/PD6 at a frequency the Timer0 time base cannot share is refused at
  compile time, naming D3/D11 and D9/D10; `millis_init()` no longer clears TCCR0A. The
  driver passes `--timebase` and the compiler binds `__TIMEBASE__` next to `__FREQ__`
  (#295).
- Every PWM HAL takes a 16-bit duty (`PWM(pin, duty_u16=...)`, `set_duty_u16()`), the entry
  the CircuitPython and MicroPython layers now use exclusively; on the AVR 8-bit channels
  it lands exactly (32768 is 50.0 %, it read 50.4 % on the Uno; pymcu-circuitpython#30).
- The two channels of one timer share its prescaler: the second `PWM()` (or a
  `set_freq()` next to a running sibling) asking for another bucket is refused at compile
  time, where it is written, through the new `claim()` intrinsic in `pymcu.types` (#300).

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
- One width per variable name, program-wide: a name typed two bytes wide at one site and
  one byte at another reached a register allocator that sizes a name once (#291).
- A module-level unannotated accumulator is typed like a local, with promotion: `n = 0`
  then `n = n + 1` over `range(300)` counted to 44 at module level and to 300 in a def.
  A written annotation keeps its width (#289).
- A comparison is decided by the values, not by the left operand's width: `count >= 404`
  with `count: uint8` tested against 148, `count < 300` against 44, and `x < n` with
  `n: uint16` read n's low byte. Sides that cannot overlap fold to Python's answer (#290).
- A single-field instance mutated inside a method's loop keeps its value and the method
  returns it: an unannotated method's `return self.value` arrived as None, and the field
  folded to the constructor's value after the loop (#292).

### Language
- `x in range(a, b[, s])`, `reversed(range(...))` and `enumerate(range(...))` over runtime
  bounds are supported; `range()` as a value is refused with a message that names the
  spellings that work (#288).
- A module-level `main()` says where the entry point's body runs: the statements written
  after it run after that body, as they do in CPython. The call used to be dropped, so
  `main(); print("END")` printed END first and main's output last, with nothing reported.
  A second `main()` and a `main()` that returns early with module-level code after the
  call are refused where the call is written (#301).

### Guardrails (was silent, now a located error)
- A call whose callee reaches the end of its body without returning is refused where the
  result is read, naming the callee. There is no `None` here, so the caller was reading the
  result temporary the expansion never wrote: an AVR PWM HAL whose prescaler selector had
  lost its `return` compiled to `MOV R4, R16` -- the low byte of RAMEND, left by the reset
  prologue -- and programmed TCCR0B = 0x3F instead of 3, which clocks Timer0 from the T0 pin
  so the output never toggles. The firmware built clean. A call written as a statement reads
  nothing and is untouched (#302).

### Diagnostics
- A line a message quotes for an EARLIER site is a line of the file the reader wrote. The
  citation now names its file (`already 3 for PD6 at main.py:38`) and `pymcu build` maps it
  back from the synthetic entry it compiles, like the header. A program that calls
  `print()` is four lines longer in `dist/_generated`, so the quoted line pointed past the
  end of the source: measured at "at line 11" for an eight-line program (#303).

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
