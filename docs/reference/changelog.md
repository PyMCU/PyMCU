# Changelog

## Unreleased

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

### HAL

- `pymcu.hal.spi`, `pymcu.hal.eeprom`, `pymcu.hal.watchdog`, `pymcu.hal.power` — all
  unsupported arch/chip fallbacks now raise `CompileError` (replaces silent `return 0` /
  missing defaults); all `match __CHIP__` blocks have `case _:` guards

---



### Language

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
