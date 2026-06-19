# Changelog

## v0.1.0a2 — Second Alpha

Python-on-bare-metal becomes substantially more faithful to Python. ~40 features and 90+
fixes across the compiler and the AVR backend.

### Language fidelity

- **Integer arithmetic promotion** (promote-and-truncate) — `+` / `-` / `*` / `<<` promote
  to the next wider type so same-width math never silently overflows (`uint8 255 + 45 == 300`).
  The declared type is a *storage width*; narrowing happens only at an explicit store or cast.
  `uint8(a + b)` is the fixed-width escape hatch. See {ref}`language-type-system`.
- **True division** — `/` now always yields a `float` (Python 3 semantics); `//` is integer
  division. Integer operands to `/` produce a warning that the float runtime is linked.
- **Real `None`** — `None` is a genuine null literal, no longer the integer `-1`; `is None` /
  `== None` checks work on reference / optional-typed values.
- Out-of-range integer **literals and folded arithmetic constants** are now rejected at
  compile time (e.g. a constant `uint8` add that overflows is a `CompileError`).
- `x ** const` with a **runtime base** lowers to repeated multiplication.

### f-strings

- `print(f"...")`, `uart.write_str(f"...")` / `uart.println(f"...")` and
  `lcd.print_str(f"...")` now accept **runtime interpolations**, lowered to direct stream
  writes — no heap, no format buffer.
- **Format specs**: `{x:02x}`, `{x:X}`, `{x:08b}`, `{x:o}`, `{x:5d}`, `{x:04d}`.

### Exceptions (overhauled)

- Full **`try` / `except` / `else` / `finally`**, with `finally` running on every exit path:
  success, caught, propagation, `return`, `break`, `continue`, and from within handlers
  (including a `break` / `return` in `finally` that discards the in-flight exception).
- **Propagation through any depth** — to the enclosing `try`, the caller, or a halt at
  `main`. The previous "single nesting level per function" limit is gone.
- **Bare `raise`** re-raises the active exception; **`ZeroDivisionError`** (builtin, code 6)
  is raised on a runtime `//` / `%` by zero.
- Builtin exceptions are **real Python builtins** — no redeclaration in the stdlib; the
  exception model rides the AVR **T-flag** (`SET` / `CLT` / `BRTS`), not `setjmp` /
  `longjmp` (superseding the a1 description below).

### Compiler / backend

- Functions may now take **more than five arguments** — overflow arguments pass through a
  fixed SRAM spill region.
- Runtime-indexed local arrays inside `@inline` functions; closures captured by `@inline`
  (including nested and `nonlocal`); slice-to-inferred-array `b = a[lo:hi]`.
- 32-bit signed widening, 32-bit multiply (`__mul32`), and signed floor-div / mod runtime
  routines corrected; arithmetic right shift sign-extends on whole-byte shifts.
- ISR-shared globals promoted to **GPIOR** registers; flash-string-by-reference; ISR context
  save now covers R20–R23 and the Z pointer; out-of-range `@interrupt` vectors are rejected.
- An unhandled propagated exception now produces a loud halt (`E:<TypeName>` to UART0, then
  `cli; rjmp .-2`) instead of silently continuing.

### Compatibility layers

- `pymcu-micropython` and `pymcu-circuitpython` shims compile unmodified under the new
  promotion / true-division model; their float-returning helpers (`millis() / 1000.0`,
  ADC-to-voltage divisions) are now correctly typed as `float`.

---

## v0.1 — First Public Alpha

### Language

- `try / except / raise / finally` — AVR targets via avr-libc `setjmp`/`longjmp`; single
  nesting level per function; exception codes imported from `pymcu.exceptions`
- `ValueError`, `TypeError`, `IndexError`, `KeyError`, `NotImplementedError` are now
  **builtins** — no import required, identical to CPython; `pymcu.exceptions` still
  exports the codes for IDE support and explicit imports from library code
- `CompileError` intrinsic — `raise CompileError("msg")` aborts compilation with a
  `CompileError:` diagnostic; never generates runtime code; used in HAL modules for
  unsupported arch/chip guards; cannot be caught by `try/except`
- `NotImplementedError` added to `pymcu.exceptions` (code 5)

### Compiler

- HAL ZCA configuration parameters typed as `const` — `Pin(name, mode)` now requires
  `mode: const[uint8]`; `if mode == 2:` branches fold at compile time; open-drain mode
  on unsupported targets aborts with `CompileError: Open-drain mode not supported on AVR`
- `ArchitectureError` C# class — maps `raise CompileError(...)` in Python source to a
  compiler diagnostic with `TypeName = "CompileError"`; emitted by all HAL modules for
  unsupported arch/chip combinations

### AVR backend

- Unhandled exception UART output — when a `raise` reaches `__pymcu_unhandled_exn` with no
  active `except` handler, the runtime prints `"E:<TypeName>\r\n"` to UART0 (if initialized)
  then halts with `cli; rjmp .-2`; only exception types actually raised in the program have
  their name strings emitted in flash; no overhead when no `raise` is present in the program;
  chips without standard UART0 (attiny85 etc.) emit only the halt loop

### RP2040 / ARM backend (new, alpha)

- New `pymcu-arm` package adds the `rp2040` target (Raspberry Pi Pico) — lowers PyMCU's
  target-agnostic IR to **LLVM IR** (`thumbv6m-none-eabi`, Cortex-M0+) and drives an LLVM
  toolchain (`opt` → `llc` → `ld.lld` → `llvm-objcopy`) to a flat flash image
  (`dist/firmware.bin`, generic crc32 boot2 at offset 0)
- Supported HAL on RP2040: `pymcu.hal.gpio.Pin` (single-cycle IO) and
  `pymcu.hal.uart.UART` (PL011) on core 0; `delay_ms` / `delay_us` via the hardware
  microsecond TIMER
- MicroPython (`machine.Pin` / `machine.UART`) and CircuitPython (`board`, `digitalio`,
  `busio`) shims compile unmodified to RP2040 firmware
- LLVM tools resolved from the `pymcu-arm-toolchain` wheel or a system LLVM
- Not yet on this backend: GC `list[T]`, exceptions, soft-float, dual-core, `@extern`, and
  every peripheral beyond GPIO/UART0 (SPI, I2C, PWM, ADC, PIO, USB, timers, EEPROM, watchdog)
- Firmware images are validated headlessly by the
  [RP2040Sharp](https://docs.pymcu.org/rp2040sharp/) emulator (`PicoSimulation.LoadFlash`) in CI

### HAL

- `pymcu.hal.spi`, `pymcu.hal.eeprom`, `pymcu.hal.watchdog`, `pymcu.hal.power` — all
  unsupported arch/chip fallbacks now raise `CompileError` (replaces silent `return 0` /
  missing defaults); all `match __CHIP__` blocks have `case _:` guards

---

### Language (core)

- `if / elif / else`, `while`, `for`, `match / case`, `def`, `class`, `return`, `pass`, `global`, `with`, `assert`, `raise`
- `for i in range(n)`, `for x in array`, `for i, x in enumerate(iterable)`, `for x, y in zip(a, b)`
- Fixed-size arrays `arr: uint8[N]`, constant and variable indexing, slice indexing
- Tuple literals, tuple unpacking, multi-return functions
- `match / case` OR patterns, guard `if cond`, sequence patterns, capture patterns
- `@property` / `@name.setter`, single-level ZCA class inheritance, `super()`, `class Foo(Enum)`
- `with obj:` / `with a as x, b as y:`, `lambda x: expr` (no capture), `nonlocal` in `@inline`
- `in` / `not in`, `is` / `is not`, `divmod`, `bitcast`, `hex`, `bin`, `sum`, `any`, `all`
- `bytes` literal `b"\x00"`, `bytearray`, `int.from_bytes`
- Raw strings `r"\n"`, `str(n)` compile-time, `pow` / `**`
- Extended unpacking `first, *rest = tup`, nested list comprehensions, `if` filter in comprehensions
- `__name__` guard (`if __name__ == "__main__":`)
- Dunder operator overloading (`__add__`, `__sub__`, comparisons, bitwise, `__len__`, etc.)
- `@extern("symbol")` — external C symbol interop with AVR ABI

### MCU extensions

- `uint8 / int8 / uint16 / int16 / uint32 / int32` typed annotations (required)
- `int` built-in maps to `int16`
- `ptr[T]` / `const[T]`, `asm("instr")`, `@inline`, `@interrupt(vector)`
- `delay_ms(n)` / `delay_us(n)`, `millis()` / `micros()`
- `__CHIP__` / `__FREQ__` compile-time constants

### HAL (ATmega328P)

- `pymcu.hal.gpio` — `Pin`: high/low/toggle/value/irq/pulse_in
- `pymcu.hal.uart` — `UART`: write/read/read_line/write_str/println/print_byte/available + RX interrupt
- `pymcu.hal.adc` — `AnalogPin`: poll + interrupt; `adc_read_temp_raw()` internal sensor
- `pymcu.hal.timer` — `Timer(n, prescaler)`, Timer0/1/2, CTC mode
- `pymcu.hal.pwm` — `PWM`: start/stop/set_duty/set_freq
- `pymcu.hal.spi` — `SPI` + `SoftSPI`
- `pymcu.hal.i2c` — `I2C` + `SoftI2C`, `write_to` / `read_from` / `write_bytes` / `writeto_mem` / `readfrom_mem_into`
- `pymcu.hal.eeprom` — `EEPROM`: write/read
- `pymcu.hal.watchdog` — `Watchdog`: enable/disable/feed
- `pymcu.hal.power` — sleep_idle / adc_noise / power_down / power_save / standby

### Drivers

- `pymcu.drivers.dht11` — DHT11 temperature + humidity
- `pymcu.drivers.ds18b20` — DS18B20 1-Wire precision temperature (12-bit)
- `pymcu.drivers.lm35` — LM35 analog temperature (ADC)
- `pymcu.drivers.hd44780` — HD44780 LCD (4-bit parallel)
- `pymcu.drivers.ssd1306` — SSD1306 OLED (I2C, 128×64)
- `pymcu.drivers.max7219` — MAX7219 7-segment display (SPI)
- `pymcu.drivers.bmp280` — BMP280 barometer (I2C)
- `pymcu.drivers.neopixel` — WS2812 NeoPixel

### Compatibility layers

- `pymcu.compat.micropython` — `machine`, `utime`, `micropython` modules
- `pymcu.compat.circuitpython` — `board`, `digitalio`, `analogio`, `pwmio`, `time` modules

### Boards

- `pymcu.boards.arduino_uno` — D0–D13, A0–A5, LED_BUILTIN
- `pymcu.boards.arduino_mega` — D0–D53, A0–A15, LED_BUILTIN
- `pymcu.boards.arduino_leonardo` — D0–D13, A0–A5, LED_BUILTIN

### Toolchain

- Programmer plugin system — custom backends via `pymcu.plugins` entry-point group
- `[tool.pymcu.ffi]` — C/C++ interop: sources, include_dirs, cflags
