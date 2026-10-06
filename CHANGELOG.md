# Changelog — pymcu-compiler / pymcu-stdlib

## 0.1.0b1 (Unreleased)

Beta 1 covers the frontend (parser, IR, diagnostics) and the AVR backend as a
matched pair: everything below was found or fixed compiling and running real
programs against AVR silicon or the AVR emulator, and it ships with a
regression test. The ARM/RP2040/RP2350, PIC, and RISC-V backends stay alpha
on purpose (see their own CHANGELOG entries), but every frontend fix here
applies to them too, since the frontend is shared across all backends.
`pymcu-avr`, `pymcu-circuitpython` and `pymcu-micropython` move to
`0.1.0b1` alongside this package; `pymcu-sdk` moves in lockstep because the
release gate requires the compiler, stdlib, and SDK to publish at the same
version.

**First frozen at `83f05312` (2026-09-15), re-frozen from `main` since.**
The 2026-09-15 freeze waited on three silent wrong-code bugs found by an
orphan-method probe sweep the night before: a nested `@inline` function
silently lost writes it made to `self` (#427), a factory function threaded
the wrong value into an unannotated field it returned (#429), and
`super().method()` miscomputed when a subclass field's constructor argument
was a constant (#430). That release was never published, and on
2026-09-25 the decision reversed: ship from `main` as it stands rather than
from the frozen branch. This section is regenerated against `main` at
`11e8bbe5` (2026-09-30), 1732 commits past `v0.1.0a10`. See
[State of the beta](https://docs.pymcu.org/state-of-the-beta/#what-the-oracle-knows-is-wrong)
for what the differential oracle still knows is wrong and discloses on
purpose, as opposed to bugs like the three above that were silent until
found.

### Hardware validation

Two unmodified Adafruit libraries ran on a real Arduino Uno with this release's
compiler: `adafruit_ssd1306` (with `adafruit_framebuf` and `adafruit_bus_device`,
Game of Life and text on an SSD1306 OLED over I2C) and `adafruit_hcsr04` (its own
simpletest). Every other Adafruit library figure in this release is a compile and
emulator measurement, not a run on a board. Details in
[State of the beta](https://docs.pymcu.org/state-of-the-beta/#what-ran-on-real-hardware-for-this-release).

### Known issues

Five silent wrong values found after the candidate was cut, none of them
announced by the compiler and none yet covered by an oracle probe or a filed
issue in this release. Each is measured, with a minimal reproducer, a
workaround that works today, and its fix status, in
[State of the beta](https://docs.pymcu.org/state-of-the-beta/#five-silent-wrong-values-found-after-the-candidate-was-cut).
All five land in beta 2.

- A top-level name reassigned from a string literal to a run-time-built
  string (`text = f"{n}"`) keeps printing the old literal.
- An f-string whose only interpolated part is a string literal
  (`text = f"{'literal string'}"`) prints the compiler's internal string id
  instead of the text.
- `list.append()` loses the fields of the instances it appends: every element
  read back reports `0` whatever the constructor stored.
- Unpacking a `(bytearray, scalar)` tuple returned by a function never
  delivers the buffer; the scalar element lands.
- Two calls to the same `@inline` function returning a tuple, indexed in one
  expression (`pair(a)[0] + pair(b)[0]`), read the second call's result twice.

### Fixed (2026-09-30, real-silicon hang, P0, RFC 0013 phase 0)

A user found and confirmed on a real Arduino Uno: an SSD1306 OLED wired over I2C
through `adafruit_bus_device.I2CDevice` never lit up, hanging in
`while not i2c.try_lock(): pass` before the first byte reached the bus. Two
independent causes, both invisible in the emulator, which always starts fresh state
at zero, unlike a real chip at cold boot:

- **ir**: `self.i2c = i2c` (a field write that ALIASES an already-constructed
  instance, the `adafruit_bus_device.I2CDevice`/`busio.I2C` pattern almost every
  Adafruit driver uses: a driver constructed inside another driver, handed the bus
  object to hold onto rather than to build) created a second, disjoint copy of the
  aliased instance's storage instead of sharing the original's, silently. A lock
  taken through one copy and checked through the other never agreed, whatever their
  initial register contents happened to be. Fixed on both the write side (the field
  write now registers as an alias of the source instance, not a fresh one) and the
  read side (a method call or read reached through the field now chases that alias
  to the real storage instead of stopping at the field's own, separate copy).
- **ir/avr**: PyMCU's generated startup code did not zero every object of *static
  duration* (module-level state, and any field of an instance a module-level object
  holds, however many hops deep) before `main` runs. On a real ATmega328P, SRAM and
  the R2-R15 register file power on with **undefined contents**, not zero; the AVR8Sharp
  emulator used in every test up to this point always starts both at zero, which
  masked this class of bug in every test that ever ran there. `busio.I2C._locked`,
  reached through `I2CDevice.i2c`, could power on nonzero on real hardware and never
  actually be zero when `try_lock()` first read it, hanging forever on a chip whose
  register allocation happened to place that state unluckily; whether a given program
  hung depended on register pressure elsewhere in the same build, which is why the
  bug reproduced on some Game-of-Life-plus-OLED programs and not their near-identical
  siblings. The frontend now tracks exactly which flattened names are of static
  duration (`AUTOMATIC` is exclusively a parameter, local or temporary a function
  itself declares; everything else, at whatever nesting depth, is static by
  exclusion) and the AVR backend zeroes every one of them, SRAM slot or R2-R15
  register home, in the boot-time clear loop. See `docs/rfcs/0013-memory-model.md`
  for the full model (phase 0 only; later phases are future work).

**Visible cost, accepted by the maintainer**: a program with static-duration state
in SRAM pays the clear loop once (about +24 bytes measured on real corpus programs,
see `pymcu-circuitpython`'s CHANGELOG), plus 2 bytes for each register that homes
static state. A program with no static state of its own (the canonical blinks
among them) pays nothing: `compat-mp-blink-toggle` stays 138 bytes,
`compat-cp-blink` stays 148 bytes.

**Test harness guarantee, going forward**: the AVR integration suite's cold boot
now starts with every register (`R0`-`R31` except `R1`) and all of SRAM filled with
`0xFF` by default, instead of the emulator's previous always-zero-on-fresh-state
default, so a program that only works by accident of implicit zeroing fails a test
instead of passing one that a real chip would not. Measured before flipping the
default: 3961 correct both poisoned and unpoisoned, 0 incorrect either way (the
poisoned and unpoisoned skip counts differ only by environment gaps unrelated to
poisoning, such as a profiler binary not being built in a given run); confirmed
green again after flipping it.

### Fixed (2026-09-29, five bugs from deciding by a name's spelling, not what it is)

Every one of these five treated a name (a function, a comment, a class) by how it was
spelled rather than by what it actually resolves to, and every one was silent: the build
succeeded and the wrong thing happened, or the right thing never happened.

- **ir**: a program's own `def sleep_ms(n)` / `sleep_us` / `delay_ms` / `delay_us` was
  never called. The call dispatcher rewrote any callee spelled that way, bare or
  `time_`/`pymcu_time_`-prefixed, to `pymcu.time`'s delay routine before ever looking
  the name up, and when that module was not loaded, to the first `@inline` function
  whose name happened to end the same way. Two calls printed `0` instead of `302` (a
  counter the user's own function incremented), at module level, from a function and
  from an `@inline`, in both front ends. The MicroPython layer's own
  `time.sleep_ms(ms: uint32)` became the 16-bit stdlib delay too, so `sleep_ms(65636)`
  slept 100 ms instead of the requested duration. **Visible consequence**: a call to
  `delay_ms`/`sleep_ms`/`delay_us`/`sleep_us` that names no defined or imported function
  is now refused at compile time; before this fix it silently compiled against whichever
  `*_delay_ms`-suffixed function the fallback found first.
- **driver**: a comment mentioning `millis_init`/`clock_init` anywhere in a source file
  used to suppress the automatic time-base preamble the same way an actual call would,
  because the check was a raw text search over the whole file, comments and strings
  included. A comment like `# millis_init() is called elsewhere` left Timer0 (or, on
  RP2350, the clock) never armed: `micros()`/`ticks_ms()` stayed frozen at `0` for the
  whole run, silently. Now parses each source file and only counts an actual `Call`
  AST node (bare name or attribute), falling back to the old text scan only when a
  file cannot be parsed.
- **driver**: the same text-substring bug reserved Timer0 for `ticks_ms()`/`micros()`
  usage: a comment merely mentioning `ticks_ms`, `ticks_us`, `micros` or `monotonic`
  anywhere in the file injected the time-base preamble for a program that never reads
  the time base at all, silently taking Timer0 from a program that wanted it for
  something else.
- **driver**: `lcd.print(...)` or `pin.input(...)` on a user's own class was detected as
  a call to the builtin `print()`/`input()` by a bare word-boundary regex that matches
  right after a dot, silently injecting a UART on PD0/PD1 the program never asked for
  and that its own driver class may already be using those pins for. A `UART` imported
  under an alias (`from pymcu.hal.uart import UART as Serial; Serial(9600)`) was not
  recognized at all, so a second, real UART got injected on top of the user's own.
  Detection is now a real `ast.Call` whose callee is the bare name (not an attribute),
  skipped when the module itself shadows `print`/`input`, and a `UART()` construction
  is recognized through its import alias or as a qualified call.
- **ir**: `uart.println(f"...")`/`.write_str(f"...")` and `lcd.print_str(f"...")`
  decided "is this the stdlib class" by `cls.EndsWith("UART")`/`cls.EndsWith("LCD")`.
  A user class merely named that way (`class BleUART`, `class SoftUART`, `class MyLCD`)
  matched too: `ble.println(f"hi {x}")` silently skipped the user's own `println()`
  method entirely and streamed straight to the console UART instead, stealing PD0/PD1
  for a wire nothing asked for; confirmed on real firmware in avr8sharp, the user
  class's own body never ran and the build succeeded as if nothing were wrong. The
  check now requires the qualified-name shape only a real stdlib instance carries (an
  underscore boundary and a `pymcu_`-prefixed module tail), which a same-named user
  class does not have. **Visible consequence**: `ble.println(f"...")` on a user class
  now either runs the user's own method for real (assign the f-string to a variable
  first) or is refused at compile time with the existing f-string-position diagnostic
  when passed directly, in place of the silent swap to the console UART.

### Fixed (2026-09-29, silent wrong value, P0)

- **ir**: a class attribute literally named `value` whose class defines the descriptor
  protocol (`__get__`/`__set__`) never called it: `.value` was recognized earlier and
  unconditionally as the MMIO register load / single-field instance collapse shortcut
  (`PORTB.value`, a ZCA wrapper's collapsed scalar), so `b.value` answered with
  un-constructed storage (`0`) and `b.value = v` wrote to storage nothing read back,
  both silently, with no diagnostic. `value` is exactly the name `adafruit_register` and
  `digitalio` use for their own descriptors, so this could reach any unmodified driver
  built on either. The descriptor check now runs before the `.value` shortcut claims the
  read/write, the same priority the existing `@property` guard already had. Oracle probe
  `080_descriptor_get_set.py`, tracked since before beta 1 as filed compiler bug #391 (a
  stale citation to an unrelated closed issue, corrected during this release's prep), now
  matches CPython and is no longer tracked.
- **ir**: three related shapes that used to silently read/write the wrong storage are now
  refused with a diagnostic instead: writing a non-data descriptor (`__get__` only, no
  `__set__`) named `value` (CPython would create a per-instance override PyMCU's
  compile-time layout has no room for); reading a descriptor through the class itself
  (`Box.value`, no instance to pass as `__get__`'s `obj`); and a descriptor whose class
  defines `__set_name__` (CPython calls it once at class-creation time, which nothing in
  PyMCU's compiler executes, so a descriptor that reads back what `__set_name__` would
  have stored answered with never-initialized storage).

### Added (2026-09-29, user-visible)

- **hal/ir/driver**: `print(x)`/`str(x)`/`f"{x}"`/`repr(x)` on a `float` now format the
  way MicroPython/CircuitPython actually do on a float32 build (`mp_format_float`'s real
  algorithm, ported instruction for instruction, not an approximation of it): 6 to 9
  significant digits, whichever is the fewest whose own round-trip matches, half-to-even
  rounding, CPython's fixed-vs-scientific threshold, `inf`/`-inf`/`nan`/`-0.0` handled by
  name. **This replaces the two-fixed-decimals output every beta-1 program printing a
  float has had until now**: `print(0.001)` used to print `0.0` and now prints `0.001`;
  `print(math.radians(180.0))` used to print `3.14` and now prints `3.1415927`. `repr()` on
  a float is `str()`; on anything else it is still refused (PyMCU has no run-time object
  model to describe an arbitrary object). Cost: 0 bytes for a program that never prints a
  float; ~3.85 KB of AVR flash for the first one that does (down from ~7.3 KB measured for
  an earlier, rejected CPython-style shortest-round-trip design), +432 B for the buffered
  `str`/`repr`/f-string-value path on top of that. AVR parts with 4 KB of flash or less
  (attiny2313/4313, atmega48/48p) fall back to a coarser compact writer that cannot fit the
  full algorithm; this fallback now triggers on `__CHIP__.flash_size <= 4096` instead of
  matching `attiny2313` by name, which had silently stopped three other tiny parts from
  building the moment a program printed a float. See
  [Limitations](https://docs.pymcu.org/limitations/) for the measured accuracy against a
  real `micropython` unix-port binary and the two designs that were tried and rejected.

### Fixed (2026-09-29)

- **ir**: a runtime (non-constant) `ZeroDivisionError` from `/`, `//`, `%` or `divmod()`,
  or a `KeyError` from a missed dict/list lookup, written directly in `main`'s own body
  (no enclosing function, no enclosing `try`) used to silently spin forever in
  `__pymcu_halt` instead of printing `E:ZeroDivisionError`/`E:KeyError` and halting like
  every other unhandled raise at that scope. The six runtime zero-check call sites and the
  `KeyError` raise all passed the wrong `unhandledInMain` flag; now routed the same way a
  literal `raise` statement already was.
- **ir**: `divmod()` now runs the same zero-divisor check `/`/`//`/`%` do (a runtime-zero
  divisor silently returned garbage instead of raising); a compile-time-constant zero
  divisor (int or float) is refused; `divmod()` not unpacked into exactly two targets is
  refused instead of silently discarding the remainder; a mixed int/float `divmod()` no
  longer mistypes the float divisor's width.
- **ir**: `x ** negative_int` on a float base (`2.0 ** -1`) computes instead of being
  refused outright; the refusal now only fires for an integer base widening to float,
  which is genuinely unimplemented. `**` and `pow()` now reach the same domain-checked
  `__pymcu_powf` for a runtime base, instead of `**` falling through to a bare
  `CALL powf` with none of `pow()`'s `ValueError` domain checks (`0.0 ** -1`,
  `(-8.0) ** 0.5`). A compile-time-constant `0.0 ** -1` or `(-8.0) ** (1/3)` is now
  refused at compile time instead of silently folding to `+Infinity`/`NaN`.
- **ir**: a float constant outside `int32`/`uint32`'s range (`int(1e10)`) is refused at
  compile time instead of folding through an unspecified C# `(int)`-cast, which measured
  as `-1` on this build host for no principled reason (a different .NET version has no
  reason to agree). A value that fits still folds exactly as before.
- **ir**: `print(e.args[0])` in an `except ... as e` handler no longer asks twice whether
  the raise carried a message (the index check already proved it non-zero and never
  returns from the zero branch); saves 6 bytes on the pattern.
- **ir**: a raise-with-message the optimizer already proved unreachable stops pulling in
  `__pymcu_exn_tail` (the crash report's `": <msg>"` trailer). It was rooted by name alone
  whenever the function existed anywhere in the program, regardless of whether anything
  still calls it; `import board` pulling in `busio`'s error-message helpers cost every
  CircuitPython program 30 bytes of unreachable report machinery for a message no
  reachable raise could produce. This is what had CircuitPython's canonical blink at 178 B
  instead of the 148 B `Direction.OUTPUT`'s PORT-before-DDR clear actually costs; see the
  blink table below.
- **docs**: the blink size table (native HAL / MicroPython / CircuitPython) is now 146 B /
  146 B / 148 B, not the stale 150 B / 150 B / 152 B, reflecting the constant-delay-counter
  fold and the exception-tail fix above.

### Added

- **docs**: which surface carries which promise, written down for the first time. The
  library authoring guide already sent authors to the native HAL and defines
  `supports.arch` in `pymcu.toml` in terms of `__CHIP__.arch`, while nothing anywhere said
  what an author could lean on, so the answer to "the native HAL can change under you, then
  what do I use" existed only as folklore. Three tiers now: `pymcu.hal.*`, `pymcu.types` and
  `__CHIP__` are the vocabulary a portable library is written against, and for the facts one
  actually needs there is no Python spelling to use instead (`__CHIP__.arch` above all, since
  neither MicroPython nor CircuitPython compiles for AVR or PIC and `sys.platform` names a
  port rather than an architecture family); the grouped peripherals are the register surface
  for programs; the loose register names, the bit constants and the rest of a chip file carry
  no promise. Beta is not frozen, so what the first two tiers promise is a changelog entry
  and a migration note, and what the third promises is the opposite. The alpha note on the
  ARM, PIC and RISC-V backends is about which chips are ready and not about this vocabulary.

- **ir**: a call that hands a buffer to a name whose `@inline` overloads all take numbers is
  refused at that line, naming the argument position and listing the overloads on offer.
  There is nothing to select in that case, and what used to happen was that whichever
  overload the registry enumerated first read the buffer's address as a number and said
  nothing. Widening one number into another is untouched, so a `uint8` argument still selects
  a `uint16` parameter. Measured over the 566 AVR fixtures and examples before it was
  written: four calls reach a non-exact selection and all four widen a number, so no program
  in the tree changes.

- **ir/stdlib**: grouped peripherals (RFC 0012). A `ptr[T]` declaration works in a CLASS
  body, so a peripheral's registers can be reached as attributes of one named class the way
  an XC8 program reaches T1CON through the Timer1 SFR block:
  `Timer1.TCCR1A.value = 0x82`, `Timer1.ICR1.value = 19999`,
  `Timer1.TCCR1B[Timer1.CS10] = 1`, `if Timer1.TIFR1[Timer1.TOV1]:`. Bit positions are
  members of the same class, so one import brings the whole peripheral and no bit name
  reaches module scope. The address may be a literal, constant arithmetic, or another
  register's name -- `ptr(TCCR1A)` -- which keeps ONE copy of every address in the chip
  file, so the grouped surface cannot drift from the loose one; an address that cannot be
  resolved while compiling is a located error naming the attribute, where the group used to
  be filed as a dead SRAM variable in silence. The class has no runtime existence: a program
  rewritten from loose register names to the grouped form produces byte-identical firmware,
  measured over the whole access surface (both widths, byte halves, read-modify-write,
  augmented assignment, constant and runtime bit index, the register inside an `@inline`
  helper and inside an ISR) and pinned in the AVR suite by two fixtures whose `.hex` must
  match. `pymcu.chips.atmega328p` ships the first group, `Timer1`. The grouped classes are
  the surface the project keeps stable; the loose module-level register names are an
  implementation detail of the HAL and may change in any release.
  Calling a group -- `Timer1()` -- is refused with a located message naming it: a group is
  a namespace over the silicon, not a type, and the call used to be accepted and produce
  nothing at all while the register accesses around it still worked.
- **stdlib**: `math.sqrt`, `math.log`, `math.exp` and `math.radians` on run-time
  software floats, and only those four: each is there because a library measured in
  this tree stops without it (adafruit_max31865, adafruit_thermistor, adafruit_sgp30,
  adafruit_mpu6050 and adafruit_lsm6ds), named by the diagnostic main gives. `log10`
  and `degrees` appear in none of the 66 upstream libraries swept, so they are not
  shipped. `sqrt` is Newton-Raphson after a scale
  reduction into `[1, 4)`; `log` and `exp` are the range reduction and the two series
  `__pymcu_powf` already carried, lifted out so each has one implementation; `radians`
  and `degrees` are a scaling multiply that folds outright for a constant angle. Every
  body lives in the compiler's embedded runtime helpers and lowers LAZILY, so a program
  that never calls one carries none of the series. Values checked against CPython
  running the same program on the emulator (oracle probe 278 in the pymcu-avr repo).
  `import math` with no call costs 0 bytes; on top of the 910 bytes of software float
  that any float arithmetic already pays, each body costs radians 8, exp 918, log 1062,
  sqrt 1452. Measured on unmodified upstream libraries: adafruit_max31865's simpletest
  went from `call to undefined function 'math_sqrt'` to a 9060-byte build, and
  adafruit_thermistor's from `call to undefined function 'math_log'` to 3374 bytes. `math.pi` and `math.e`
  are deliberately NOT defined: a module-level float constant in an imported module
  becomes storage nothing initialises, so they would read back 0.0 instead of refusing.
- **parser**: a PEP 484 forward-reference annotation -- the type named as a string
  literal, `def __enter__(self) -> "BH1750":` -- is the type it names. The quotes come
  off in `AnnotationText`, the one place both front ends reach, and the name inside is
  resolved and refused exactly like a bare one. The compiler used to answer
  `unknown type '"BH1750"' (did you mean 'BH1750'?)`, naming the answer and refusing it
  in the same sentence; adafruit_bh1750's simpletest now builds unmodified at 6488 bytes.

- **ir**: compile-time 2-D grids the way CircuitPython writes them:
  `g = [[v] * W for _ in range(H)]`, `g = [bytearray(W) for _ in range(H)]` and
  `self.g = <same>` in `__init__` lower to ONE flat fixed array of `W * H`
  elements, with `g[y][x]` emitting the same `y * W + x` index arithmetic the
  hand-flattened `bytearray(W * H)` spelling produces. Both dimensions fold
  exactly like a fixed array's size -- literals, `const` names, module
  constants, or a constructor argument that is a literal at every call site.
  `len(g)` is `H`, `len(g[y])` is `W`, `for row in g` lowers to a row-index
  loop when the body only touches `row[x]`, `for x in g[y]` iterates one row,
  and `r = g[y]` binds a row view usable only for `r[x]` / `len(r)` /
  `for x in r` in the same block. A row is a view, not a value: passing it,
  returning it, storing it in a field, comparing it, `in` on it, slicing it or
  the grid, appending to it and rebinding `g[y]` are each refused with a
  located diagnostic on both front ends, and `[[0] * W] * H` is refused
  outright because CPython's spelling aliases a single row. The
  `compat-cp-life` demandant written with `self.cells[y][x]` produces a
  byte-identical I2C stream to the hand-flattened fixture on the emulator and
  to CircuitPython 10.3.1.
- **parser**: an f-string adjacent to plain string literals now folds into one
  `JoinedStr`, the same merge CPython's parser makes — `f"...{x:x} is not "
  "correct! ..."` keeps the literal pieces as text parts instead of refusing the
  adjacency. This is the spelling `adafruit_seesaw` uses for its chip-id raise, and
  a raise message built that way replays its runtime pieces like `print` does
  (format specs included — an int under an `f` spec prints `5.0`, matching CPython).
- **ir**: `buf += src` on a fixed `bytearray` is the in-place concat CPython gives
  the spelling: the buffer's compile-time size grows by the source's length and the
  source's bytes store into the new tail — the same lowering as
  `buf[len(buf):] = src`. This is how `adafruit_seesaw.write` builds its command
  buffer (`full_buffer = bytearray([reg_base, reg])` then `full_buffer += buf`).
  The bump happens once while compiling, so the `+=` is accepted only when it sits
  in the same run-time branch context as the buffer's declaration — appending
  inside a loop or a non-folded conditional the declaration does not share would
  grow the buffer a different number of times than the declaration runs, and is
  refused with that explanation. Per-callsite inlining keeps `len()` correct when
  one call site appends and another does not: the `buf=None` expansion of
  `adafruit_seesaw.write` still writes a two-byte transaction while a sibling
  expansion appends.
- **ir**: `struct.pack(fmt, v...)` bound to a name produces a fixed `bytearray` of
  `calcsize` bytes with the fields stored into it — `pack_into`'s writes onto a
  fresh name — and the same call works as a slice-assign source
  (`cmd[offset:] = struct.pack(">I", pins)`). An open-ended slice assign
  `buf[a:] = src` takes its length from the source's compile-time length, so the
  start may be run-time. `struct` also covers the 4-byte codes `I`/`i`/`L`/`l`
  under `<`/`>`/`!`, widening each byte to `uint32` before it shifts so a 24-bit
  shift cannot truncate. A `struct.pack` call in any other value position is
  refused: on a heap-free target there is nowhere to put the bytes.
- **ir**: a module discovered through a function-scope import now gets the same
  optional-import marking the dependency graph applies at top level, so a
  `try: from micropython import const / except ImportError:` inside one — the
  pinmap idiom inside `adafruit_seesaw` — folds to the found branch instead of
  failing on the stub handler's nested `def`.
- **ir**: void calls to the same subroutine with structurally identical constant
  arguments now share one synthesized zero-argument stub. An init sequence like the
  SSD1306's calls `writeto(addr, temp, 2)` dozens of times and re-marshaled the same
  immediates at every site (~10 bytes of LDI before each RCALL); the sites now
  collapse to a bare `RCALL __pymcu_callstub_N` and the stub holds the
  constant-bearing call once. The stub forwards the T flag through the ordinary
  error-propagation protocol, so a failing callee still reports to the original
  site. Measured across the AVR corpus: 26 programs shrink by 4–194 bytes
  (`adafruit-ssd1306-unmodified` 4626 → 4432), `examples/lcd` is size-neutral, and
  nothing grows. Per-call the stub costs one extra CALL/RET pair — a character
  write on `examples/lcd` is +17 cycles (+0.7%), a 513-byte SSD1306 frame write is
  +9 cycles (+0.02%).
- **ir**: RFC 0009 decision 7, runtime form — an unnarrowed `Optional`/`Union`
  value used where CPython faults on None lowers to a member dispatch instead of
  a compile-time refusal. Arithmetic (`+ - * / // % **`), unary `-`/`~`, the
  ordering comparisons, `len()`, a subscript, and an argument bound for a
  non-Optional parameter each emit one leaf per member combination; a live
  member runs the operation at its own width and the None leaf raises TypeError
  with CPython's exact wording through the RFC 0005 deferred-print channel
  (`unsupported operand type(s) for *: 'NoneType' and 'float'`). Union fields in
  Model B slots and instance-array elements store the payload at the widest
  member plus a sibling tag byte, and `-> Union[...]` methods may outline.
  The unmodified `adafruit_dht` simpletest compiles and prints
  `Temp: 74.3 F / 23.5 C    Humidity: 55.0% ` — identical to CPython; a sensor
  read that produces None faults at `temperature_c * (9 / 5) + 32` the same way
  CPython faults. A value proven non-None still emits byte-identical code.

### Fixed

- **stdlib**: the counter that walks a bus transfer's byte count is as wide as the count.
  The RP2040 and RP2350 `write_bytes` had the count widened to `uint16` and the counter left
  at `uint8`, which is worse than the truncation it replaced, and worse than "the loop never
  ends": measured on a probe of exactly that shape rather than assumed, **there is no loop
  at all**. The compiler folds `i < n` to always-true, because a `uint8` cannot reach 300,
  so what is emitted is a body ending in a bare backward jump with no compare -- and the
  statement after the loop is deleted as dead code, so the failure takes everything behind
  it with it. The `if i == n - 1` that asserts STOP is never reached either. The truncation
  at least returned control. This is the same shape as `delay_ms(500)` against a `uint8`
  parameter on PIC: a range fold decides the comparison, the loop disappears, and the code
  after it goes with it. The source sweep could not see this end of the loop, since it read
  the parameter list and the counter is a local; it now checks both, with a probe for each
  shape it must catch and two it must leave alone.

- **stdlib**: a bus transfer's byte count holds a buffer longer than 255. The AVR I2C entry
  point declared `n: uint8` and the layer handed it `len(buf)`, so a 128x32 SSD1306's
  512-byte framebuffer arrived as `512 & 0xFF == 0`: the loop ran zero times, `show()` put
  one byte on the wire, and the display stayed blank while every command byte before it was
  correct. Measured on the emulated Uno, 4 -> 4, 255 -> 255, 256 -> **0**, 300 -> **44**,
  512 -> **0**, and now every one of them arrives whole. This is the PASS-THROUGH form of a
  lying width declaration: the parameter is compared with nothing, it is used at its declared
  width on a value that comes from the CALL SITE, so the body reads as blameless and the
  source sweep for the comparison form cannot see it. Neither can the compiler's own refusal,
  which fires on a literal the parser built: `take(300)` against `def take(n: uint8)` is
  refused naming the 44 that would arrive, while `take(len(big))` for the same 300 compiles
  and receives the same 44. Fourteen entry points across I2C, SPI, soft-I2C and the
  RP2040/RP2350 buses carried it, with their loop counters; a new source sweep
  (`tests/stdlib/test_bus_transfer_count_holds_a_buffer.py`) is what found the fourteenth
  after twelve had been fixed by hand, and an AVR fixture pins what a device receives. Four
  programs in the tree grow, all four of them users of the feature, and every byte is one
  16-bit counter's `CLR`, `CPC` and `ADIW`: i2c-readfrom-mem +14, compat-mp-i2c-rw +32,
  compat-mp-spi-rw +38, compat-mp-uart-readline +12. PyMCU#511.

- **ir**: a parameter of an `@inline` expansion lost to a module global of the same name.
  Every shim in the compatibility layers takes parameters with ordinary names -- `buf`,
  `data`, `addr`, `value`, `n`, `pin` -- so a program that bound a module-level name matching
  one of them silently changed what a library it did not write computed, with nothing to see
  at the call site. Two resolutions were wrong and they had different reach. `len(param)`
  asked the bare name before the frame's own binding, so the global's length was emitted as a
  constant, from anywhere. A subscript `param[i]` followed the ENCLOSING FUNCTION's spelling
  first, which inside a function binds nothing and is harmless, but at MODULE level is
  `main.<name>` -- a real alias to a module global of that name -- so the expansion read and
  transmitted a different object entirely. That is why the same shim was correct from inside
  a function and wrong from module level with the source identical. Both now ask the
  expansion's own binding first; the bare and enclosing lookups keep their places one step
  later and still resolve names the expansion does not bind. Found through I2C:
  `machine.I2C.writeto` is `def writeto(self, addr, buf: bytearray)` and calls
  `write_bytes(addr, buf, len(buf))`, so a program with its own `buf` put that buffer's
  length, and at module level its contents, on the wire. The discriminating experiment
  throughout is a RENAME: the layout does not move and the answer changes, which a shared
  storage slot could not do. PyMCU#512.

- **ir**: a buffer held anywhere but a local picked the SCALAR `@inline` overload, and the
  call sent the buffer's ADDRESS truncated to eight bits. `i2c.writeto(self.addr, self.temp)`
  put `3C 02` on an emulated Uno's TWI lines where the program says `3C 80ae`, and the `02`
  was `lo8(&self.temp)`: putting a module array in front of the buffer moved the byte to `05`
  and `09` with it. Nothing was said at any point. `self.buf = bytearray(n)` is the shape
  every MicroPython I2C driver is written in -- `ssd1306` and `sh1106` carry it literally --
  so a driver written that way compiled, ran, talked to the bus and sent garbage, and the
  source gave a reader nothing to see, because the same buffer in a LOCAL already picked the
  right overload and the call is spelled identically in both places. Overload selection typed
  the argument with `InferExprType`, which for a buffer answers with its ELEMENT type. Six
  spellings of one buffer were wrong this way and all six are fixed: a field built by
  `bytearray()`, a field declared with a size, a field one object down
  (`self.i2c_device.buffer`), a buffer written in the class body and read either way
  (`D.BUF`, `self.BUF`), a `memoryview` and a slice of one -- which was never about fields,
  a view over a module-level buffer was wrong too -- and a buffer returned from a method.
  Firmware is byte-identical across all 566 AVR fixtures and examples. PyMCU#503.

- **ir**: `ptr(buf)` on an ordinary array is refused instead of compiling to a read of the
  array's first byte. ptr()'s constant-base arm exists for a hardware register, where the
  base is an address the compiler resolves; an array name is an ordinary variable and fell
  past it into the runtime-address path, which EVALUATES its argument. The pointer then held
  `buf[0]` widened to 16 bits -- a small number that looks plausible -- and a write through
  it landed that many bytes up from address 0: register space, another global, the stack,
  with no diagnostic anywhere. An SRAM array lives at a label the assembler assigns, not at
  a number this pass can compute, so the refusal is what can be offered: located, naming the
  array, and saying that ptr() takes a hardware register or a numeric address.
- **ir**: `print()` of a 16-bit register printed its LOW BYTE alone -- `TCNT1.value` holding
  0x1234 printed as 52, with nothing said. print picks its formatter from the argument's
  width and had arms for a variable, a temporary and a constant with a uint8 default for
  everything else; a register read is none of the three. Assigning the same read to a
  `uint16` local first printed it whole, which is what made the truncation look like the
  register's fault.
- **ir**: a field holding a runtime-sized `bytearray(n)` stores the arena OFFSET, and stored
  it in one byte. The class layout is derived from the original class body, where such a
  field is recognized as a buffer field and kept out of the layout, so the write took the
  uint8 default; the lowering that synthesizes it already asked for uint16 and nothing read
  the annotation. On any part with more than 256 bytes of arena the second runtime-sized
  buffer in a class starts past 255: an offset of 300 wrapped to 44 and the two buffers
  silently aliased, a write to `self.b[0]` landing inside `self.a`. A width written on an
  assignment now widens the field's and never narrows it.
- **ir**: the message for a member of an instance that collapsed to a number names the
  parameter instead of the number. An instance passed to a parameter that declares no type
  arrives as a plain number, so every member access on it fails -- `o.a[i] = v`, `o.a[i]`
  and a bare `o.x` alike. "'a' is not a member of a numeric value" described the number and
  left the one thing that makes the difference, a `: C` on the parameter, off screen. When
  the member is a field some class writes, the message now names the parameter, names the
  classes that declare the member, and spells the annotation. The collapse itself is
  unchanged.
- **ir**: a field holding a register pointer lost its element width, so `.value` on it
  wrote TWO bytes into I/O space and landed the second one on the neighbouring register.
  Three of the four ways to bind such a field dropped the width -- a bare `ptr(addr)`, a
  `ptr[T]` parameter, and a register name the chip file exports -- and the field then kept
  whatever the class scan had guessed for it, which is UINT16 because what such a field
  holds is an address. A `tccrb` field at 0x81 wrote TCCR1C on every access, a `timsk`
  field at 0x6F wrote TIMSK2, with no diagnostic. The one binding that worked was a
  `-> ptr[T]`-annotated selector function, because there the width travelled with the
  return annotation; that is why the PWM HAL's `pwm_select_ocr()` pattern never tripped on
  it. The width now travels with the address on all four. The same omission made a
  `ptr[uint16]` PARAMETER write only one byte of its pair.
- **ir**: `self.reg: ptr[uint8] = TCCR1B` is accepted. An annotated instance member whose
  type is a subscripted generic was read as an array annotation, so the element type was
  taken for a size expression and the program was refused with "Array size 'uint8' is not a
  compile-time constant" -- about a program with no array and no size in it. The same
  subscript on a parameter and at module level was already accepted. The annotation now
  also FIXES the field's element width, which is the direct spelling for what previously
  needed a selector function: `self.cnt: ptr[uint16] = ptr(0x84)` writes the pair.
- **ir/avr/stdlib**: a 16-bit AVR register write put the LOW byte out first, so the value
  that reached the hardware was built from a stale latch. An 8-bit core reaches a 16-bit
  peripheral register pair through one shared TEMP byte: writing the high half only fills
  TEMP, and writing the LOW half is what commits both at once. Every 16-bit store did the
  opposite -- a constant store was split into low-then-high byte copies by the IR generator,
  and a runtime store lowered to `STS low`, `STS high` in the AVR code generator -- so
  `TCNT1.value = 0x1234` committed the pair as (whatever TEMP held) | 0x34 and the high byte
  that followed only refilled TEMP for the next access; measured on silicon it read back
  0x0334. Nothing in the shipped stdlib tripped on it because the timer, PWM and servo HALs
  write the byte halves by hand in the datasheet order, with one exception now fixed:
  `timer1_clear()` cleared TCNT1L before TCNT1H, the order a READ takes, so the counter came
  out of `clear()` holding the high byte of whichever 16-bit access ran before it. Reads are
  unchanged and stay low-byte-first, which is what latches the high half. What makes this
  worth a release note rather than an internal correction is RFC 0012: the grouped peripheral
  surface puts the 16-bit name within reach of user code (`Timer1.OCR1A.value = 1500`), and
  that surface is the one the project promises to keep.

- **ir/frontend**: a package's own `__init__.py` reaches its names. Three shapes CPython runs
  did not compile. `import pkg` followed by `pkg.f()`, where the `__init__` only re-exports
  `f`, was reported as a call to an undefined `pkg_f`: the function is compiled under the
  module that DEFINES it, and the member read of the same name already chased that while the
  call did not (#468). `from pkg import sub` written inside `pkg/__init__.py`, the absolute
  spelling of a submodule re-export, ended the whole build in "Cyclic dependency detected"
  with no line to look at -- a module that imports itself is a no-op in Python, and the
  self-edge in the dependency graph was not -- and the same statement then answered the
  question "does the package bind this name?" with itself, so the submodule beside it was
  never loaded. And `from pkg import sub` at the use site, where the package really does bind
  `sub` as its submodule, stopped the re-export chase at the package and mangled `sub.f()` to
  `pkg_f`: both spellings of the package's own line arrive as `import pkg.sub as sub`, which
  no symbol list mentions. Measured against CPython over fourteen package layouts; the one
  shape still refused, `import pkg` then `pkg.sub` with an empty `__init__`, is the one
  CPython refuses too.

- **frontend**: `import types` no longer resolves to `pymcu/types.py`. The stdlib alias that
  answers `import time` with the pymcu file of that name answered `types` with PyMCU's own
  type-system module, which is read by the compiler rather than loaded -- so it went through
  the file path, was parsed as ordinary source, and reported `class ptr(Generic[T])` as a
  SyntaxError inside the stdlib against a program whose only line was the import (#482). The
  Adafruit guard `try: from types import TracebackType / except ImportError` does not catch a
  SyntaxError, so the rest of the guard never bound either. The refusal now says what `types`
  is and that the width names live in `pymcu.types`, a different module.

- **ir**: a call to a name an imported module does not carry says so. `utime.localtime()`
  came out as "call to undefined function 'utime_localtime' (typo, or a missing import?)",
  which names a symbol the program never wrote and offers two answers that are both wrong:
  the spelling is right and the import is already there and worked. A compat module that
  resolves and does not carry a member is a decision its layer records in its parity
  allowlist -- no RTC on this part, no interpreter to report on -- not an omission. The
  message names the module and the member, and lists what the module does define, its
  classes' methods excluded (#475).
- **ir**: five ways a string stopped being a string once it left the place it was written.
  Each one printed a number where CPython prints text, or -- worse -- decided a branch on
  that number, and none of them said anything. They were one shape seen from five sites:
  the value of a string on this target is its interned id (or, for a one-character literal,
  its character code), and each site asked the value what WIDTH it was instead of whether it
  stood for text at all.

  - `if x == "abc":` on a name bound to a multi-character string deleted the `then` branch
    from the image (#438). A condition does not go through the value path where two string
    operands are already compared by text; it becomes a conditional jump over the values,
    and `str` is a one-byte slot while an interned id needs two, so the slot read back 0
    against the literal's own 256 and the always-false range fold fired. The one-character
    case, whose id IS its character code and so fits the byte, answered correctly the whole
    time, which is what kept it hidden. Both texts were in the compiler's own tables;
    only that path never asked for them.
  - `print(hex(255))` wrote 257 and `print(bin(10))` wrote 258, the interned ids of `"0xff"`
    and `"0b1010"` (#393). `print(str(42))` was right for the unrelated reason that `str()`
    is one of the shapes print recognised by syntax. A value carrying its compile-time text
    is now written as that text whatever expression produced it; `pow()` and `**`, which
    fold to numbers, still print numbers.
  - `print(s[2])` on a runtime string wrote 48 instead of `0` (#399). The character at a
    position is a one-character string in Python, which print knew for a compile-time string
    and could not look up for a runtime one. A `bytearray` element stays a number, as in
    CPython.
  - `chr(n)` lost its character across a name and a `return`: `c = chr(69); print(c)` wrote
    69 and `print(make(66))` wrote 66 (#436). A constant `chr()` now carries its
    one-character text on the value, and the functions whose every `return` is a `chr()` are
    recorded, so a call to one goes to the byte writer.
  - `print("mono" if k == 0 else "none")` wrote 260 and 261 on a real Uno, and binding the
    expression to a name first did not help (#378). The same two texts chosen by an if/else
    STATEMENT always printed correctly: that one goes through the merge which records the
    alternatives. Written into a write, a conditional expression now lowers as the condition
    plus a literal write on each side, in the shared streaming helper so `uart.write_str`
    and `println` get it too; bound to a name, both arms count as bindings and the id is
    stored in the slot a read dispatches on. A condition the compiler can decide still folds
    to the chosen arm.

- **stdlib**: an LED blink for the PIC16F84A stopped compiling over a UART it never mentions.
  `pymcu/hal/__init__.py` re-exports all five peripherals, so `from pymcu.hal.gpio import Pin`
  alone reaches `pymcu.hal.uart` and, on a PIC14 target, the `pic14_uart` dispatcher. That
  dispatcher answered a part with no USART with a module-level `raise`, which fires on the
  import rather than on a UART operation, and the 84A has no USART. The refusal is right and
  stays; what moves is when it fires. The `else` arm now binds entry points that refuse when
  they are called, so the message lands next to what the program actually asked for and names
  the parts that do have a USART, where a `UART(9600)` on the 84A used to be answered with
  "call to undefined function 'uart_init'". Reaching past `UART` into the chip HAL --
  `from pymcu.hal.pic14.pic14_uart import uart_write` -- is refused too, so no route to a
  byte on the wire builds clean on a part that cannot send one; `hal/uart_text.py` takes its
  write primitive from the chip module directly, as the note at the top of that file already
  said it should, so the writers it emits unconditionally no longer drag the refusal in.
  The sibling PIC14 facades -- `adc.py`, `pwm.py`, `timer.py` -- already dispatched inside
  the class body for this reason; the UART was the one that did not. Firmware for every PIC
  program that does have a UART is byte-identical.
- **stdlib**: `uart_init` on the PIC16F18877 takes a `uint16` baud selector. The selector is
  the rate in hundreds, so 38400, 57600 and 115200 arrive as 384, 576 and 1152 and the
  declared `uint8` holds none of them. Those three arms were unreachable by the declaration
  and taken in practice only while an `@inline` argument arrived unnarrowed; narrowing turned
  them off, and `uart_init(1152)` stopped writing SP1BRGL/SP1BRGH at all, leaving the
  generator at its reset value. Same defect, same shape and same repair as the timer
  prescaler below. The `uart_init(baud: uint8)` in `hal/avr/uart/atmega328p.py` has it too
  but is not reachable -- the AVR selector routes every part that has a USART to
  `hal/avr/uart/avr.py`, whose signature is `const[uint16]` -- so it is left alone and
  reported.
- **stdlib**: `timer0_init` takes a `uint16` prescaler on the PIC14, PIC14E and PIC18 HALs,
  as it already did on PIC12 and AVR. The three declared `uint8` while testing
  `prescaler == 256`, an arm a uint8 can never take, and `Timer.__init__` hands them a
  `uint16`. The mismatch was invisible while an `@inline` argument reached the body
  unnarrowed: 256 matched an arm the declaration says is unreachable, and the register was
  configured by luck. Narrowed, 256 became 0, no arm matched, and `Timer(0, 256)` configured
  nothing at all -- which is how the dead arm was found.
- **stdlib**: `delay_ms` is declared `uint16`, and now every architecture behind it is. The
  per-architecture helpers (`_delay_ms_pic12`, `_delay_ms_pic14`, `_delay_ms_pic14e`,
  `_delay_ms_pic18`, `_delay_ms_riscv`) each took a `uint8` and walked a `uint8` counter, so
  a delay longer than 255 ms could not be expressed at all: `delay_ms(500)` was 244 ms on the
  five of them, against the `uint16` the AVR and ARM paths already honoured. With the inline
  narrowing missing on top of that (see the `ir` entry below), it was not 244 ms either -- it
  was a loop with no exit. The counters are `uint16`, which costs one byte of RAM and about
  ten instruction cycles per millisecond on the PIC cores.
- **stdlib**: `_delay_ms_pic14e` derives its loop counts from `__FREQ__`, the way
  `_delay_ms_pic14` already did. It was a single table calibrated for 32 MHz by a comment
  that described a different count from the one in the code (11 outer turns, not the 10 the
  comment worked from), so it ran 5.8% long there and was wrong by the frequency ratio
  everywhere else. There are now cases for 1, 2, 4, 8, 12, 16 and 32 MHz -- the HFINTOSC
  speeds this family offers -- with 32 MHz as the fallback, because that is what RSTOSC
  selects out of reset and what the Curiosity Nano's default config word leaves running.
  Each case carries its cycle arithmetic and the error it measures; the worst is -0.40% at
  1 MHz, where 16 cycles of loop frame are already 6.4% of a millisecond, and 32 MHz lands at
  +0.013%. Verified on a simulated PIC16F15244: 4,000,261 Tcy between LED edges for a
  requested 500 ms, which is 500.033 ms. `_delay_ms_pic14`'s four cases are recalibrated for
  the wider counter by the same arithmetic, and all four now land inside 0.1% where 4 MHz
  used to be 0.8% long.
- **ir**: an integer constant bound to an `@inline` parameter of declared width arrived
  unnarrowed, so the same callee body saw a different value depending on whether it was
  expanded or called. A real subroutine gets the narrowing from the ABI -- the value is
  copied into the parameter's slot and the slot is as wide as the declaration -- but an
  inline expansion has no slot, and the literal was substituted as written. The cost was
  silent wrong code, not a wrong number: `delay_ms(500)` reaches the stdlib's
  `_delay_ms_pic14e(ms: uint8)`, whose body is `while i < ms` over a uint8 counter, and
  bound to 500 instead of 244 the range fold read the test as one the counter can never
  fail, deleted it, and deleted the rest of the enclosing loop with it. The Curiosity Nano
  blink wrote its LED once and then span forever; the LED-off half of the blink was not in
  the hex at all. Measured on a PIC16F877A, the same program compiled through a real
  subroutine stored 0xF4 and kept its exit test, which is the disagreement the fix removes.
  Only the four explicit fixed-width spellings narrow (`uint8`, `int8`, `uint16`, `int16`):
  an unannotated parameter has no declared width and is left alone.
- **ir**: the six comparison dunders were never dispatched from the position a reader
  writes them in. An `if` (and a `while`, and each operand of an `and` / `or`) does not
  lower its comparison through the expression path; it becomes a conditional jump straight
  over the flattened instance handles, which are never written, so `if a == b:` compared
  two zeroed slots and answered "equal" for every pair of objects while the same comparison
  assigned to a name dispatched correctly. `__eq__`, `__ne__`, `__lt__`, `__le__`, `__gt__`
  and `__ge__` now reach their method in every position. Two further receivers reached no
  method either and now do: an instance bound at MODULE level, whose binding is filed under
  its bare name while the lookup asked for the synthesized module body's scope (`print(a + b)`
  at top level answered 0 for any operator dunder, arithmetic included), and a class-typed
  FIELD (`self.lhs == self.rhs`), which is the shape a driver writes.
- **ir**: a comparison involving an instance of a class that defines no comparison dunder
  answered from the same never-written handles: `a == b` was true for every pair, `a is b`
  likewise, `a == 0` was true for any object, and `a < b` / `a < 1` were false for all of
  them. It now answers what CPython answers. `==`, `!=`, `is` and `is not` fall back to
  identity, which is decided at compile time because every instance owns its own static slot
  (`b = a` is recognised as the same object, and nothing that is not an instance is ever
  equal to one); an ordering has no fallback, so it is refused with a located diagnostic
  naming the method the class would need, as CPython raises `TypeError` for it. The other
  operand has to be one the compiler can be certain about -- another instance, or a literal --
  so a plain name, a field, a call result or a subscript keeps the path it had.
- **ir**: `max()` and `min()` over instances compared the flattened handles and reduced to
  whichever zero won, so `max(a, b).n` printed 0 even for a class defining `__lt__` and
  `__gt__`. Refused with a located diagnostic, in the pairwise spelling and over a sequence
  (`max(xs)`) alike. `sorted()` and `in` over a list of instances already refused.
- **ir**: a `match` class-pattern capture whose name collides with a module global did not
  bind. The capture was filed under `main.<name>` while every read of the name resolved the
  global, so the arm read the global's old value, with no diagnostic and correct-looking
  firmware. A capture now takes the key every read of that name resolves to — the module
  global at module level, as CPython binds it there, and the function-qualified spelling
  inside a function — and consults the inline prefix, so two expansions of one `@inline` no
  longer share one capture slot. The bind also drops the folded value, the alias and the
  class of the binding it shadows: `f"{captured}"` was refused as "an instance of 'C'" when
  the shadowed global held an instance, and the arm's reads folded to the global's constant.
  `case C() as name` on the subject itself no longer files a self-alias, which made the
  compiler spin instead of emitting.
- **ir**: a subscript write on a class attribute was claimed unconditionally by the
  compile-time class-dict accumulator that exists for the Adafruit CV pattern, so
  `Store.buf[0] = 5` on a class-level `bytearray` emitted no store and the matching read
  folded to the 5 out of a phantom dict entry. The program agreed with itself at that one
  subscript and read the allocation's zeros everywhere else -- from a loop, from a runtime
  index, from another function. The accumulator now runs only when the attribute IS a dict
  or set binding.
- **ir/avr**: a class constant used as a bit index missed the direct bit test, so
  `if TIFR1[Timer1.TOV1]:` materialized the bit into a register and compared it -- ten bytes
  where `SBIS` answers in one -- while the same condition written `if TIFR1[0]:` was free.
- **hal/avr**: `I2C.writebyte()` returned nothing, so a NACKed transaction was invisible
  to the caller. It now returns `1` on success, the failing TWI status (`0x20`/`0x30`) on
  a NACK and `0xFF` on a bus timeout, with an early STOP — the same contract
  `write_to()` already had. Every status-returning I2C method is `@inline`, so a caller
  that ignores the result compiles to the same bytes as before; only a caller that reads
  the status pays for the check.
- **ir/avr**: an unhandled exception reported `E:<TypeName>` on UART0 and dropped the
  message the raise carried. The report is now `E:<TypeName>: <message>` when the raise
  had one — literal messages and deferred ones (f-string, concatenation, call) alike —
  so `machine.I2C`'s `OSError("[Errno 5] EIO")` and `adafruit_bus_device`'s
  `ValueError("No I2C device at address: 0x3c")` are visible when they kill a program.
  The message printer and its tail are emitted only when a *reachable* raise stores a
  message: a program that merely imports a module containing `raise X("...")` — say
  `busio` — does not grow, and a program with no message raise at all is byte-identical.
- **driver**: the UART preamble is injected when the program reports a raise with a
  message, not only when it calls `print()` — an unhandled raise reports on UART0 even in
  a program that never prints.
- **ir**: a `bytearray` field passed to a real subroutine now hands over the array's base
  address, not its first byte. The arg reached the marshal as a `Variable` naming the
  field's flat storage — `self.temp` through an inline binding — and a `Variable` copies
  contents where a pointer is needed. Member-held arrays and array variables evaluated to
  a `Variable` now marshal as `ArrayBase`, and the outliner types a live-in that the body
  dereferences (or forwards to a pointer parameter) pointer-wide even when its declared
  tag is the element type. Seen as `i2c.writeto` writing `0x00 0x00` for the SSD1306
  init stream.
- **ir**: a `raise` no longer forces its function to expand inline at every call site —
  the exception propagates through the ordinary T-flag protocol like any other call, so a
  raise-bearing subroutine can be shared. `raise CompileError` stays inline-only, since it
  is a lowering-time refusal that must not fire for a program that merely imports the
  module.
- **ir**: an exception message's bare `{x:x}`/`{x:X}` piece uses a minimal hex writer
  (~200 bytes) instead of the generic `{fmt}` machinery and its 32-bit division helpers
  (~900 bytes). Padded widths and other radixes still take the full formatter.
- **ir**: keyword arguments to an outlined method bind against the declared parameter
  list, not the synthesized `self_<field>` prefix — `o.writeto(a, buf, start=1)` binds
  `start` correctly — and a `bytes`/`b"..."` literal written inline as a call argument
  gets an addressable buffer, so the `I2CDevice` probe `writeto(addr, b"")` reaches the
  subroutine.
- **ir**: an unannotated class method's return type is inferred by the same
  return-join pass module-level functions already had, so `self.v = self._read()`
  with `def _read(self): return 300` lays `v` out as `uint16` and the outlined
  callee returns both bytes instead of truncating to `LDI R24,44; RET`
  (PyMCU#489). A return the pass cannot type -- a member read, a subscript,
  `return None` on a reached path -- still leaves the method unannotated, the
  RFC 0009 case unchanged.

### Changed

- **ir**: `__new__` and `__init_subclass__` are refused where the method is written. Both
  compiled clean and ran nowhere: a class returning a cached instance from `__new__` got a
  fresh one, and a base counting its subclasses counted none. There is nothing to intercept
  -- an instance is laid out in static storage with no allocation call, and a class is a
  compile-time layout with no class object and no creation event -- and no file in the
  stdlib, either compatibility layer, or the AVR example and fixture corpus defines either
  hook.
- **ir**: `__del__` is reported as a warning at its definition, for every class the program
  constructs. It is emitted nowhere and cannot be run: storage is static, nothing collects
  an instance, and `del` is refused for that same reason, so there is no moment a destructor
  could run at. It is not refused, because the MicroPython layer mirrors upstream's
  `machine.Timer.__del__` faithfully; the warning fires only where an instance is actually
  built, so a program that merely imports the module stays quiet.

### Added (2026-09-25 to 2026-09-29)

- **ir**: `e.args` in an `except ... as e` handler reads as the tuple of the `raise`'s
  constructor argument.
- **frontend**: a `raise ClassName()` call with no argument is distinguished at parse time
  from one with an argument, instead of both reaching the IR the same way.
- **frontend**: a parameter every call site hands a string literal or a `str` value infers
  `str`, instead of needing an explicit annotation.
- **ir**: a list literal a function `.append()`s to is allocated on the heap, so its
  lifetime survives the function returning it.
- **ir**: `enumerate()` accepts a compile-time constant `start` argument.
- **hal/avr/pwm**: an exact Timer1 PWM channel can be retuned at run time and keeps its
  duty cycle across the retune (the CircuitPython buzzer idiom).
- **hal/avr/uart**: `reinit`, `deinit` and `tx_empty` (#451).
- **hal/irq**: `save_and_disable_interrupts` / `restore_interrupts` (#353).

### Fixed (2026-09-25 to 2026-09-29)

129 fixes landed in this window (full subjects: `git log f3b21bf1..6f8d2149`), 116 of them
in the IR generator. Grouped by what kept going wrong:

- **Width and type inference**: an unannotated accumulator, slot, or return now takes the
  width its first store (or its inference) actually needs instead of guessing from the seed
  it began with; an integer literal from 2^31 up is a `uint32`; a `const[float]` parameter's
  default is folded as the constant it is; a `str` parameter compares and holds by flash
  address consistently.
- **Buffers and parameters that shadow**: a scalar local or parameter no longer silently
  aliases a module-level buffer of the same name (and vice versa for `bytes`/`ptr`); a
  buffer handed to a parameter never binds to one that holds a plain number; passing,
  slicing, `len()`-ing and subscripting a buffer parameter now all reach the caller's
  argument rather than a stale copy.
- **Evaluation order and side effects**: call arguments, keyword-to-positional binds, item
  assignments and sequence-argument elements are each evaluated exactly once, in the order
  written, instead of sometimes twice or reordered.
- **`None` / `Optional` tracking**: a `None` reaching a name through a call result, a field
  read, or an installed module's global prints as `None` and narrows correctly through
  `and`/`or` and `is None`, instead of losing the tag partway through.
- **Inline/ZCA frames**: an `@inline` expansion is named after the function it expands in,
  starts from a clean frame for dunders/setters/`super()`, and a raise inside one now
  unwinds through the caller's `finally` and re-raises the saved code instead of whatever a
  scratch register happened to hold.
- **Module-level globals**: a `const`, a walrus target, an `asm()` operand and a nested
  field of a module-level instance are now filed under the module/frame that actually owns
  them, closing several cases where a name crossing a frame silently became local.
- **Numeric builtins**: `divmod()`, `pow()`, `sum()`, `reversed()`, `isinstance()`, `chr()`
  and a bare builtin name (no call) each had a specific wrong-output or wrong-diagnostic
  case fixed.
- The remaining fixes are in the frontend (5, mostly comprehension/optional diagnostics),
  the optimizer (3), the SDK (2), the AVR I2C HAL (1), and the driver (1).

Every one of the 129 ships with a regression test in the same commit ([[feedback_cada_bug_un_test]]
in this project's convention).

### Zero cost
- A call argument that HOLDS a compile-time constant binds the parameter as that constant, not
  as a variable containing it. The difference used to be only whether the argument mentioned a
  local: `f(int(s * 1000))` bound a constant and `x = int(s * 1000); f(x)` bound a variable,
  so every callee that dispatches on the value -- the calibrated delays,
  `pwm_prescaler_for_freq`, `claim()`, any `match` on a `const` parameter -- lost its constant
  path, and a `const` parameter refused the call outright. A local computed from another local
  counts, and so does a narrowing cast of one while the value fits. Measured on a millisecond
  delay written with locals: 681 bytes and the generic counted subroutine, against 484 and the
  calibrated loop (#327).
- A `range()` bound written as an EXPRESSION decides the loop. Only a literal or a name folded
  before, so `range(total_us // 60000000)` was a run-time counter loop over a 32-bit bound;
  1814 bytes on a 380-byte program. A folded count of at most eight unrolls, and an empty
  range emits nothing (#326).
- An unsigned 32-bit division no longer carries the modulo wrapper it never calls, and the
  shared division core keeps its remainder in the call-clobbered X and Z registers instead of
  saving five callee-saved registers on every call. A program that only divides used to pay
  for `__mod32` too, because the build splices whole assembly files and both lived in
  `div32.S`; the modulo wrapper now sits in its own `mod32.S`. Measured on the HC-SR04
  probe (`us * 17 // 100`): 952 bytes before, 888 after (#408).
- A `bytearray(...)` whose size folds at compile time no longer reserves the arena. The
  driver used to scan the source text for a `bytearray(` call and reserve the board-default
  256 bytes of SRAM whenever the argument did not look literal, so
  `adafruit_ssd1306`'s `self.buffer = bytearray(((height // 8) * width) + 1)` -- which folds
  to a fixed 513-byte array -- printed `Arena: reserved 256 B` and paid it on a 2 KB part.
  The compiler now reports on its own output whether an arena allocation actually lowered,
  and the driver stages the allocator only then; a foldable size reserves nothing, and a
  genuinely runtime-sized `bytearray(n)` keeps its reservation and the arena shim.
- A constant `for` loop now weighs its body before unrolling. Any constant iterable of at
  most eight elements expanded unconditionally before -- `range`, a list, tuple or string
  literal, a named sequence, `enumerate`, `reversed`, a slice, a returned fixed array --
  so `for y in range(8): for x in range(32):` wrote the inner loop out eight times, and a
  small loop over calls like the SSD1306 `init_display` command table copied the helper
  for every step. An unroll now happens only while the body is cheap; an expensive body
  lowers to a counter loop, and the constant sequence it iterates is materialized once in
  flash instead of being inlined per step. A loop variable that must stay a compile-time
  constant -- one passed to a `const` parameter, say `Pin(n, Pin.OUT)` -- still unrolls
  whatever the body costs, and a flash table no surviving function references is swept
  after dead-function elimination rather than emitted. Measured on the unmodified
  Adafruit SSD1306 driver: 664 bytes smaller; the 32x8 Conway's Life program builds for
  the Uno with SRAM to spare.
- A member array made by an inlined constructor was allocated twice: the mark that says
  `self.buffer = bytearray(513)` lives as one contiguous array was cleared between
  functions, so `enumerate(self.buffer)` in a later method fell back to 513 slot variables
  (`display_buffer__0..512`) no instruction ever wrote -- a second, phantom framebuffer.
  On the Life program that put static data past the ATmega328P's 2048 bytes of SRAM.
  Residency marks now accumulate for the whole program: the storage outlives the function
  that created it, so the mark does too.

### Silent wrong code
- A `return None` on a path the program can reach, in a function declared `-> X` and
  compiled as a shared subroutine, is refused at the return instead of emitting `ret` with
  whatever the return register held -- the caller read a stale R24 as its answer. The
  `@inline` expansion refused the same shape already; the outlined path was the hole
  RFC 0009 measured. A bare `return` is the same statement to Python and is refused the
  same way on both paths. `-> Optional[X]` stays the spelling the refusal names, reserved
  for the runtime-tagged return the RFC brings.
- A program whose module level can end parks the CPU instead of returning into nothing. The
  entry function is reached by `RJMP` with an empty hardware stack, so the `RET` its trailing
  return lowered to popped two bytes past RAMEND and jumped wherever they pointed -- on
  silicon a reboot loop or a wild PC. Every reachable return in `main` (the module body and
  `def main()` alike) now jumps to a shared `__pymcu_halt` (`cli` + spin), the avr-libc
  `_exit` idiom: interrupts off, last outputs held. A body that cannot fall through
  (`while True:`) has that return deleted as unreachable, so a never-ending program is
  byte-identical.
- An unhandled `raise` written in the entry function halts with its name instead of returning
  from a function that has no caller. It lowered to `SET; RET`, so the RET popped a return
  address that was never pushed and the chip ran off into whatever the top of SRAM held; the
  documented `E:<TypeName>` and halt was reached only by an exception returning into main from
  a callee (#339).
- A keyword argument clears the None an earlier expansion of the same `@inline` function left
  on that parameter. Any call that let `parity` default to None made the NEXT call's
  `parity=Parity.EVEN` read as None, so the `match` inside took the `case None` arm and a
  UART asked for 7E2 was programmed 7N2 (#324). A `match` subject that folded to a constant
  no longer matches `case None` at all: the arm was lowered as a comparison against a value
  with no representation, and which arm ran was decided by whatever register it read.
- An unannotated field is as wide as the widest value the constructor assigns. It was laid
  out as a byte whatever was stored in it, so `self._period = uint16(1000000 // uint32(hz))`
  read back as `20000 & 0xFF`; `adafruit_motor.servo` put 689 us on the pin where 1000 was
  asked for (#322).
- An import alias belongs to the module that wrote it. One flat table was shared by every
  module, so two files aliasing different things to the same name got whichever was
  registered first, and a wrapper class ended up constructing itself (#320).
- Registering one routine at two interrupt vectors is refused where it is written. The
  second registration overwrote the first, leaving that vector on the bad-interrupt handler
  with its enable bit set: a quadrature encoder on INT0 and INT1 was deaf to one of its two
  pins (#325).
- A global an ISR writes stays in SRAM. The AVR backend homed it in R2-R15, which every ISR
  prologue saves and every epilogue restores, so the handler's write was undone on RETI and
  an encoder counted every edge and reported 0 for ever (#328, fixed in pymcu-avr).
- A `raise` inside an `except` handler is reachable only when the `try` raised, and is no
  longer treated as an unconditional abort of the function: the handler is lowered under
  the same dispatch as any other, so `try: s.index(...) except ValueError: raise` in
  `adafruit_pixelbuf.parse_byteorder` keeps the raise conditional instead of ending the
  expansion for every path.
- A parameter that starts `None` and is then rebound keeps neither mark: `p = None` made
  `if not p:` fold, but `p = "GRB"` later still carried the none-flag, so a
  `super().__init__(byteorder=p)` bound the callee's parameter as `None` and the string
  was dropped. The mark is cleared the same way any other rebind clears it
  (adafruit_pixelbuf `PixelBuf.__init__`).
- A compile-time string read through a second `@inline` hop keeps its text. The prefixed
  lookup returned the constant's value without the text it was interned with, so
  `len(s)` inside a callee's callee was told the argument "must be a fixed-size array or
  list literal" while holding "GRB".
- `if not p:` on a parameter bound to `None` folds to the true arm, the same fold
  `if p is None:` already had -- `if not pixel_order:` in `PixelBuf.__init__` picks the
  default byteorder instead of testing an unwritten slot at run time.
- A `for` unrolled over constant strings binds the loop variable to the text, not the
  interned id. The element folder ran first and reduced "PD2" to 256, so
  `for name in ["PD2", "PD3"]: print(name)` printed the ids where the names were meant.
  A one-character element keeps its character code too, the same spelling a literal
  assignment gives it (oracle probes 009/010).
- A parameter or local that shares its name with a module-level array wins the
  subscript: `def f(s: const[str])` next to `s = bytearray(3)` made `s[i]` inside the
  function load `main.s`, so the callee streamed the caller's global and ignored the
  argument it was passed. The HAL's own `uart_write_str` is that shape -- every
  `print()` after a runtime `s` was bound re-emitted the buffer instead of the text
  (oracle probe 070). The load path now applies the local-binding rule the indexed
  store path has had since #458/#460.
- A field a module-level object got from an inline call's return reads the same value
  inside a function as at module scope. `pwmio.PWMOut.__init__` stores
  `self._real_frequency = self._pwm.frequency()`, an inlined call whose result temp
  aliases the folded constant: the field assignment checked the temp itself for a
  constant instead of chasing the alias, emitted a real store, and dead-store
  elimination then removed that store once the module-scope reads had folded -- so
  `print(pwm.frequency)` printed 61 in `main` and 0 inside `def show()`. The fold now
  walks the alias chain, and a module-instance field a function reads but nothing can
  fold is promoted to real global storage instead of being left a dead-stored local.
  This is the field `adafruit_motor.servo` computes `_min_duty` and `_duty_range` from.
- A `return` expression sitting in dead code behind a folded `if` no longer overwrites
  the result alias the taken arm already set. `pymcu.hal.pwm.PWM.frequency()` reads
  `if self._exact: return pwm_t1_exact_frequency(...)` then
  `return pwm_bucket_frequency(...)`: on a mode-14 pin the first return is selected
  and binds the result temp to the exact frequency, but the trailing dead return was
  still walked and rebound that alias to the bucket arm -- so once field stores
  chased aliases (previous entry) a `Timer1` PWMOut reported 61 Hz while emitting an
  exact 50 Hz. Variable and temporary results now respect the same
  `afterUnconditionalReturn` guard the constant arm already had.
- A `float` constant passed through a property setter binds its parameter.
  `Servo.fraction`'s setter receives `value = angle / actuation_range` as a
  compile-time `0.5`, but the setter binding switch had a case for every value kind
  except `FloatConstant`, so the body read an unwritten slot and `_duty_range *
  value` computed 0 -- `s.angle = 90` wrote `_min_duty` for every angle. A
  `FloatConstant` now binds like the other constant kinds, truncating to the
  parameter's integer annotation when it has one.
- A comparison between two compile-time floats folds to its boolean result instead
  of `FloatConstant(0.0)` -- the catch-all arm of the constant-float fold answered
  `0.0` for every operator, so `if not 0.0 <= value <= 1.0:` on a bound float
  constant read the range check as failed and `Servo.fraction`'s setter raised on a
  legal value. Comparison operators now fold to `0`/`1` while arithmetic keeps its
  float result.
- `buf[i] += v` on a `bytearray` parameter stores through the pointer like
  `buf[i] = v` already did. The store-back dispatch for an augmented index
  assignment never checked the buffer-parameter table and fell through to the bit
  writer: `buf[2] += 1` wrote a bit into the pointer register itself -- the buffer
  byte never changed -- and a runtime index refused `Bit index must be constant`
  on a legal statement. `xs[i] += v` on a `list[T]` took the same wrong exit; both
  now lower to the indirect store, and the read-modify-write keeps the element's
  width, so a `list[uint16]` no longer truncates the sum to a byte.
- A `const[T[N]]` table whose initializer is computed -- `[0]*256 + [11,22] +
  [0]*40`, `range(N)`, a concat of constants -- carries its real bytes instead of
  a table of zeros. Only a literal `[...]` populated the table before, so the
  flash image had the right length with every read answering 0.
- A `list[T]` declared at module level is one slot under the global's bare name.
  The declaration filed it as `main.xs` while everything else that names a module
  global -- appends, `xs[i]` reads and writes, `len(xs)`, the GC root -- spelled
  it `xs`, so the pointer lived in a slot nobody read and the list the program saw
  stayed empty; inside an `@inline` expansion the receiver was also loaded as a
  one-byte pointer, corrupting the heap header at a shadow address in low SRAM.

### Language surface
- `-> Optional[X]` on a real subroutine is a runtime-tagged union return (RFC 0009
  phase 1): `X | None` and `Union[X, None]` spell the same thing. A function whose
  None-ness is decided at run time returns its payload in the ordinary result
  registers and a one-byte member tag in the next register of the return run; the
  caller's local stores payload and tag together. `r is None` / `r is not None` /
  `if r:` / `r or default` read the tag, and `if r is None: return` narrows `r` to
  `X` on the fall-through. Reading an unnarrowed runtime-tagged name is refused at
  the line -- CPython's `TypeError` made a compile-time fact. A `return None` on a
  reached path of a `-> X` function stays refused. When every reached path is
  provable at compile time no tag exists and the build is byte-identical to before;
  the AVR backend declares the capability as `--return-tags`, and a `.mir` carrying
  tags fed to a backend without it stops the build instead of dropping the tag.
  Optional fields, Optional parameters on real subroutines and multi-member unions
  are phases 2 and 3.
- `bytearray(n)` with a runtime `n` allocates from a static arena (no `free()`, AVR only)
  instead of being refused, wherever the compiler can prove the statement runs at most
  once: a module-level statement not in a loop, or an `@inline __init__` reached only
  through inlining from one. Anywhere else it is refused, naming the reason. `x[i]`,
  `x[i] = v` and `len(x)` work. A new
  `MemoryError` (raised by the allocator on overflow, catchable like any other builtin
  exception) and a new `pymcu build` line (`Arena: reserved <N> B ...`) report the
  reservation. See `docs/rfcs/0004-arena-allocator.md`.
- Rebinding an imported module name to an instance shadows the alias for every
  access kind, matching CPython. `from adafruit_motor import servo` then
  `servo = servo.Servo(pwm)` then `print(servo.fraction)` used to refuse the
  read as `Unknown module member: adafruit_motor_servo_fraction`; writes
  already saw the instance (#467).
- An unannotated field whose first store is a string literal or `bytearray(...)` /
  `bytes(...)` is that kind, not uint8. `self._message = ""` then a `str` setter is
  the same field (adafruit_character_lcd); `self._gpio = bytearray(n)` then a buffer
  setter is the same field (adafruit_74hc595). An int store then a str store is still
  refused.
- `for p in (a, b)` over already-constructed instances unrolls the same way
  `for p in self._pins` does. `pin.direction = OUTPUT` through that loop
  variable is the `@property` setter, not an assignment to a method.
  Last construct unmodified `adafruit_character_lcd` stopped on.
- `bytearray(self._n)` after `self._n` holds a compile-time integer is a
  fixed buffer of that size (`self._gpio = bytearray(self._number_of_shift_registers)`).
- A constructor is not a shared subroutine. Explicit `@outline` on `__init__`
  was already ignored; an undecorated large `__init__` used to outline anyway,
  so a class-typed parameter took its annotation (`digitalio.DigitalInOut`)
  instead of the argument (`adafruit_mcp230xx.DigitalInOut`). `pin.high()`
  then became HAL gpio with a runtime bit index. Last construct unmodified
  `adafruit_character_lcd` (I2C backpack) and `adafruit_mcp230xx` stopped on.
- `from time import struct_time` resolves to a stdlib stub with the nine
  CPython field names. Adafruit RTC drivers import it in a try used only for
  typing; `pymcu.time` exists, so that try does not raise `ImportError`.
  Construction from a 9-tuple (`struct_time((y, m, d, ...))`) is not this stub.

- `from collections import namedtuple` / `collections.namedtuple(...)` is a compile-time
  class factory. A module-level `Name = namedtuple("Name", ("a", "b"))` becomes a ZCA
  class with those fields, `__len__` and `__match_args__`. The bound name
  is the class, not the typename string -- adafruit_irremote's
  `UnparseableIRMessage = namedtuple("IRMessage", ...)` constructs `UnparseableIRMessage`.
  Last construct unmodified `adafruit_irremote` stopped on.
- Descriptor protocol `__get__`/`__set__` receive `obj` as the owning instance, even
  when the parameter is annotated with a typing-only placeholder (`I2CDeviceDriver`).
  The methods are expanded at the call site so the argument's class substitutes (#419).
  Last construct unmodified `adafruit_register` `RWBits` stopped on, which is what
  `adafruit_ina219` and `adafruit_veml7700` reach.
- A class-body dict is a compile-time lookup table, the same as a module-level one.
  `self.gain_values[gain]` is a fold or a compare chain; mixed int/float values
  (VEML7700's `0.25` / `0.125`) make the lookup a float. Last construct unmodified
  `adafruit_veml7700` stopped on.
- A constant tuple assigned to a field is a fixed array, the same as
  `self.buf = [0, 0, 0]`. Counted as a uint8 the class became one-field and
  `self.scale[n]` compiled as a bit index. Last construct unmodified
  `adafruit_dps310` stopped on (`self._oversample_scalefactor = (524288, ...)`).
- A `str` parameter that received a compile-time string keeps the text, of any
  length. Only a one-character literal used to, so `struct.calcsize(struct_format)`
  inside an inlined descriptor constructor refused `"<HH"`. Last construct
  unmodified `adafruit_pca9685` stopped on (`StructArray(0x06, "<HH", 16)`).
- `[None] * n` / `[0] * n` is a fixed SRAM array of n slots. A local
  `coeffs = [None] * 18` (adafruit_dps310) and a field
  `self._channels = [None] * len(self)` (adafruit_pca9685) are indexable;
  None is a 0 slot, so `if not xs[i]` still reads empty.
- `x = a, b, c` is `x = (a, b, c)`. The same comma wrap `return a, b` and
  `a, b = 1, 2` already had. Adafruit framebuf writes
  `fill = (color >> 16) & 255, (color >> 8) & 255, color & 255` without
  parentheses around the whole RHS.
- `buf[i:i+n] = bytes(fill)` is an element-wise copy of length n. The start
  may be known only at run time; the length is compile-time (`i:i+3`). The
  dest is a module `bytearray` or an instance-member buffer (`self.buf`),
  and `bytes(named_seq)` unwraps to that sequence. Adafruit framebuf RGB888
  fill writes `framebuf.buf[i:i+3] = bytes(fill)`.
- `"mod.Cls"` is the same type annotation as unquoted `mod.Cls`. A quoted
  forward reference was already the bare name (#261); a dotted class is the
  spelling `busio.I2C` already has. Adafruit si7021 writes
  `obj: "adafruit_si7021.SI7021"`.
- A `try` whose `except` is not `ImportError` still keeps the imports its
  body resolved. The inner Adafruit TYPE_CHECKING guard
  (`except NotImplementedError: from circuitpython_typing.pwmio import PWMOut`)
  no longer drops `PWMOut` (#480) and no longer loads the stub when pwmio is
  there (#481).
- `import os` / `from os import uname` resolve to a stdlib stub. `uname()` is a
  compile-time five-field record of `__CHIP__` (sysname `"PyMCU"`, machine the chip
  name, with an `RP2040`/`RP2350` token on those parts). `"Linux" not in uname()`
  and `"RP2350" in uname().machine` fold. `listdir` / `getenv` stay undefined --
  there is still no filesystem (#466). Last construct unmodified `adafruit_dht`
  stopped on.
- Under a declared compat layer (`stdlib = ["circuitpython"]` / `["micropython"]`),
  `if` / `match` conditions on `sys.implementation.name`, `sys.implementation.version[i]`,
  `sys.platform` and `os.uname()` / `uname()` fields fold at compile time, answered with
  the strings a real board of that layer reports (RFC 0007, PyMCU#266). The
  adafruit_requests guard `if not sys.implementation.name == "circuitpython":` drops its
  dead `import typing` branch rather than keeping it. With no compat layer the fold does
  not fire and the firmware image is unchanged.
- A non-literal raise message (f-string, concatenation, call) compiles as a deferred
  print: runtime pieces are stored at the raise, and `print(e)` / `str(e)` / `e.args[0]`
  replay them (#435). A program that never binds `as e` is unchanged to the byte.
- `isinstance(x, (tuple, list))` folds from the receiver's known shape: a compile-time
  sequence is true, a scalar is false (#423). Last construct unmodified
  `adafruit_ht16k33` matrix stopped on.
- A function that fills a `bytearray` and returns it is expanded at the call site:
  the buffer is laid out in the caller's frame and the assignment aliases it (#464).
  Last construct unmodified `adafruit_bmp280` stopped on.
- A `Protocol` named in a `Union` on an `@inline`/constructor parameter is structural.
  `DigitalInOut` is not named `ROValueIO`, but it has the `.value` property the protocol
  asks for, and CPython accepts the call; the call site now does too (#465). Last
  construct unmodified `adafruit_debouncer` stopped on.
- `pow(x, 2.5)` with a runtime float base lowers to IEEE-754 single `powf`. Integer exponents
  still unroll to multiply. This is the last construct unmodified `adafruit_tcs34725` stopped
  on (#463).
- `raise X(...) from Y` is accepted in both front ends and compiled as `raise X(...)`. There
  is no traceback and no `__cause__` on this target, so the clause is discarded after it is
  parsed -- the same treatment a non-call raise message already gets. `e.__cause__` and
  `e.__context__` on a bound exception are refused by name (#434). This is the construct
  `adafruit_irremote`, `adafruit_pixelbuf` and `adafruit_mcp230xx` stop on.
- `from typing_extensions import Protocol` is a no-op, the same way `from typing import
  Protocol` already is (#444). `typing_extensions` is resolved by the type system, never
  loaded as a file (#462). `circuitpython_typing.device_drivers` opens with that import,
  unguarded, and three Adafruit libraries stopped there.
- `from __future__ import annotations` is a no-op (#452). `__future__` is a compiler pragma
  that enables nothing PyMCU does not already do; it is skipped like `typing`.
- `m[x, y]` is refused by one sentence naming the construct, at the first index, on both front
  ends. It was `Expected "]"` from one and, from the other, the generic tuple refusal, whose
  advice to build a fixed list for indexable storage is not what a reader indexing a matrix is
  doing. It is the no-runtime-tuple limit reached through a subscript, and the message now says
  so and points at the method the dunder stands for (#352).
- The optional-import idiom every CircuitPython driver opens with works, and picks the branch
  that is true. An import inside a `try` that catches ImportError was never discovered, so the
  module was not loaded and the name it binds was undefined at the call site -- while the flag
  the same block sets bound fine. The try is now a compile-time branch: the module decides it,
  nothing at run time can, and the body's `_USE_PULSEIO = True` used to be emitted whether or
  not `pulseio` existed, so the flag said True on a build that had no such module (#351).
- `super().__init__(a)` applies the base constructor's defaults. The binding loop stopped at
  the end of the argument list it was given and left the rest unbound, so the base body read
  its own defaulted parameter and was told the name "is read here but never assigned,
  imported, or received as a parameter" -- about a parameter, one line under its declaration.
  A required parameter no argument reaches is now named instead (#350).
- A keyword argument binds to a base-class method: `super().__init__(pwm, min_pulse=500)` is
  line 110 of `adafruit_motor/servo.py` and the normal way a driver subclass forwards. It used
  to reach the value path and print "Unknown Expression type: KeywordArgExpr", the name of a
  class in the compiler about a word the program does not contain. Any callee that still
  cannot bind one now names the argument instead (#349).
- `except (A, B):` catches either, which is what the refusal used to tell the reader to write
  by hand. The alternatives are compared against the error code in turn and all reach the one
  handler body; a single type still emits the one comparison and one skip it always did, so
  every existing firmware is byte-identical. It is the optional-import fallback that opens
  `adafruit_dht`, `adafruit_hcsr04` and `adafruit_motor` (#346).
- An annotation with a dotted name, a nested subscript or an empty `[]` inside its brackets is
  read whole, so the sentence that names the construct is reached instead of `Expected ']'` at
  a column inside the annotation. `Union[int, List[int]]` and `Optional[digitalio.DigitalInOut]`
  now get the union refusal, and `tuple[X, ...]` is told that `...` is not a type annotation, on
  both front ends at the same line and column. An unclosed bracket points at the bracket rather
  than at the end of the file (#345).
- `self.column, self.row = 0, 0` unpacks into attributes, as it already did into names. The
  right-hand side is snapshotted before any store, so an attribute swap is still a swap, and
  each target is then written through the assignment it would have been on its own line. It
  used to die two tokens past the comma as "Expected newline or end of block" (#344).
- A base class spelled `module.Class` is read as the class it names. The C# parser used to
  stop at the dot and ask for a closing bracket, so `class NeoPixel(adafruit_pixelbuf.PixelBuf)`
  was refused where the Python front end built the firmware; when the module really is absent
  the reader is now told which one (#343).
- An annotation spelled `module.Class` is read as the class it names, so `p: busio.I2C` says
  what `p: I2C` already said. The parser used to stop at the dot and ask for the closing
  bracket of the parameter list, which is about a bracket in a program whose brackets are
  balanced; a dotted name that ends in a typo is still reported, by the sentence about type
  names (#342).
- A list literal accepts a trailing comma, as a call, a parameter list, a dict and a set
  already did. It was the one bracketed construct without the guard, so `[1, 2,]` was
  reported as a missing expression pointing at the closing bracket -- and every formatter in
  the CircuitPython ecosystem writes that comma on a collection split over several lines
  (#341).
- `from <package> import <submodule>` works even when the package's `__init__` mentions the
  submodule's name in a comment, which is what refused `from adafruit_motor import servo`
  (#323).
- A constant inside a nested class is readable: `Outer.Inner.A`, and `busio.UART.Parity.ODD`
  through the declaring module. The scan never registered a nested class body's attributes
  and the read resolved one hop at a time (#319).
- A `for` over a constant list of STRINGS unrolls, so
  `for pin in (board.D2, board.D3, board.D4)` -- the CircuitPython idiom for a row of pins --
  binds each pin as a compile-time constant instead of being refused as a non-integer (#308).
- A HAL module can put its own interrupt on a pin: a handler named as a value resolves in the
  module that defines it, and `compile_isr()` accepts a vector composed from an `@inline`
  table instead of only a literal (#321).
- `Pin.mode()` has the reading half its signature advertises, on AVR and on PIC; every
  `match __CHIP__.name:` in the PIC14 GPIO layer refuses an unsupported part instead of
  falling off the end (#312).
- A generator expression as the DIRECT argument of `all`, `any`, `sum`, `min` or `max`
  unrolls at compile time over an iterable whose length is known -- a tuple or list
  literal, a bound const sequence, a `range` of constants, a compile-time string or a
  fixed-size array. `all` and `any` short-circuit like CPython, so a deciding element
  ends the walk and later elements are never evaluated; `sum` honours its `start`
  argument and a `for ... if` clause filters. In any other position the refusal names
  the five reductions and says why: there is no iterator object to hand out. This is
  the line `adafruit_pixelbuf` validates a colour with --
  `all(0 <= component <= 255 for component in val)` -- and the last thing unmodified
  `neopixel` + `adafruit_pixelbuf` stopped on: `NeoPixel(board.D6, 4)` then
  `pixels[0] = (1, 2, 3)` compiles and drives the wire in GRB order on the emulated
  Uno.
- `for chunk in s.split(sep)` -- and `for i, chunk in enumerate(s.split(sep))` --
  unrolls a compile-time string's pieces. The receiver and separator must be texts the
  compiler knows (a literal, a module constant, or a parameter bound to one, through
  nested `@inline` calls included), `maxsplit` a compile-time int; anything else is
  refused naming the reason, because a runtime split needs a heap to hold the pieces.
  `split()` in a value position is refused: there is no list to hand back. This is the
  loop `adafruit_framebuf.FrameBuffer.text()` wraps each line in.
- `for i, ch in enumerate(s)` over one compile-time string iterates its characters at
  any length. Eight or fewer still unroll to the same compile-time `(index, char)`
  pairs as before; a longer string used to refuse with "past the 8 cap" and now runs a
  counter loop over the string's interned flash copy, the same lowering `for ch in s`
  already took past its own cap. `ch` arrives as a runtime `uint8` char code that
  `ord()` and font-table arithmetic accept unchanged, and `len(s)` / `s[i]` inside the
  body still fold. This is the inner loop `text()` runs per line, so a
  `display.text()` line longer than eight characters compiles unmodified.
- The pure methods on a compile-time string fold where the program is compiled:
  `s.strip()` / `lstrip()` / `rstrip()` (optional chars argument), `s.index()` /
  `s.find()` (a miss raises a catchable `ValueError` / answers -1),
  `s.startswith()` / `s.endswith()`, `s.count()`, `s.replace()`, `s.upper()` and
  `s.lower()`, plus `len(s)`, `s[i]`, `needle in s` and `s == "lit"`. The text
  survives `super().__init__`, keyword arguments, a `p = None` parameter rebound to a
  string, a `cond else` ternary and a second `@inline` hop, which is the chain
  `adafruit_pixelbuf.parse_byteorder` runs "GRB" through. A miss inside
  `s.index(...)` is a real raise, so the `try/except ValueError` around it catches it.
- `getattr(mod, "name", default)` on a module folds at compile time to the member or
  the default -- the CircuitPython `getattr(board, "SCK", board.D13)` spelling. The
  attribute name must be a literal and the receiver a module; `getattr` on anything
  else stays refused, there is still no runtime type info to walk.
- A tuple-return element that is itself a fixed literal sequence reaches the caller as
  a compile-time sequence: `return 3, (r, g, b), False, False` then
  `bpp, byteorder_tuple, has_white, dotstar_mode = parse(...)` binds
  `byteorder_tuple` to `(r, g, b)` so `byteorder_tuple[i]` folds, and `dotstar_mode`
  is still the constant `False`, so `if dotstar_mode:` drops its branch instead of
  lowering it (adafruit_pixelbuf `parse_byteorder` / `PixelBuf.__init__`).
- `isinstance(x, slice)` folds to False: nothing in PyMCU is a runtime slice, so the
  `elif isinstance(pixel_order, (tuple, list))` / `isinstance(index, slice)` guards in
  `adafruit_pixelbuf` take the arm the value selects.
- An annotation may be written through an alias: `ColorUnion = Union[int, uint8]`
  binds the name at compile time -- including inside a discarded `if TYPE_CHECKING:` /
  compat-layer guard -- and a parameter annotated `x: ColorUnion` resolves it to the
  same members as the spelled-out union (adafruit_pixelbuf `PixelBuf.__init__`).
- `for b in buf` iterates an arena-backed buffer byte by byte when the buffer reaches
  the loop through an `@inline` parameter binding or a field --
  `self._post_brightness_buffer` forwarded to `neopixel_write` is the shape
  `adafruit_pixelbuf.show()` uses to push the frame.
- `open(name, mode)` compiles to a romfs handle (RFC 0008): no filesystem exists on
  the chip, so the driver embeds named files as flash blobs (`files = [...]` under
  `[tool.pymcu]`, or automatically when a literal `open()` names a file in the
  sources) and the compiler resolves the call at compile time. `name` and `mode`
  must be compile-time strings; read modes only. The protocol is `read(n)` with a
  compile-time `n` — the result is a view over the blob that `[i]`, `len()` and
  `struct.unpack(fmt, f.read(n))` fuse onto — plus `readinto(buf)`, `readline(max)`,
  `seek`/`tell`, `close`, `with`, `os.stat(name)` and `os.listdir(dir)`. A runtime
  name, a runtime mode, a write mode or a file nothing embedded is a compile error
  that names the case. `adafruit_framebuf.FrameBuffer.text("...", x, y, color)`
  renders through `BitmapFont` and its `open("font5x8.bin", "rb")`, byte-identical
  to CPython on the emulated Uno.

### Diagnostics
- A refusal about a parameter annotation, a return annotation or an undefined base class
  inside an imported module names THAT module's file. Both checks are deferred until every
  module has been scanned, and both ran with no module in scope, so the line came from the
  definition and the file from the entry program: a union inside `adafruit_motor/servo.py`
  line 52 was printed as `main.py:52`, in a `main.py` fifteen lines long (#347).
- A refused comprehension says which thing is unsupported. A comprehension of class instances
  was told it has a filter, and sent the reader looking for an `if` that is not there (#307).
- A diagnostic about a module-level line above `def main():` is reported at the line it is on.
  The driver mapped the injected preamble by one offset for two insertion points and picked
  the larger, so the number came out one early while the snippet text was right; the
  debugger's line map was off by one over the same region (#311).
- A refusal raised inside a method body the compiler expands for you names the file that
  defines the body: a base-class method reached through `super()` or
  `Base.method(self, ...)`, a dunder like `__setitem__` behind `obj[i] = v`, or the wrapper
  an interrupt handler is lowered through. Each expansion switched the symbol prefixes but
  not the source file, so `bytearray(17 * len(self.i2c_device))` inside
  `adafruit_ht16k33/ht16k33.py` was reported as `segments.py:60` and an `isinstance()`
  inside `adafruit_pixelbuf.py` as `main.py:1`.
- A diagnostic from the pre-scan transforms of an imported module names that module's file:
  `yield` inside a method of `adafruit_irremote.py` was refused as `main.py:1`, because the
  transform runs on a bare AST that carries no file and the module's line was rendered
  against the entry file.

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
- `Pin.mode(Pin.IN)` gives an input with the pull the pin asked for, not the level it
  was driving (a pin made an input after `high()` stayed at 5 V on the Uno);
  `mode(IN_PULLUP)` sets the pull-up instead of writing 2 into the direction bit, and
  `mode(OPEN_DRAIN)` is refused (#309). `digitalio.deinit()` releases without pull.
- The hardware `I2C` raises the AVR's internal pull-ups on SDA and SCL before enabling
  the TWI, the `digitalWrite(SDA, 1); digitalWrite(SCL, 1)` Arduino's `twi_init()` does:
  a module with weak or missing pull-up resistors answers instead of floating the bus
  and timing out the first probe. Two `SBI` on the 328P (4 bytes), and a program that
  never asks is byte-identical. `I2C(pullups=False)` opts out for a 3.3 V bus;
  `board.I2C()`, `busio.I2C` and `machine.I2C` get the default through the same init.
  With the pull-ups up, the CircuitPython layer's `busio.I2C` also checks the wiring the
  way upstream does: it reads both lines and raises
  `RuntimeError("No pull up found on SDA or SCL; check your wiring")` when either is held
  low. `machine.I2C` and `bitbangio.I2C` do not check, matching their upstreams.
- The two channels of one timer share its prescaler: the second `PWM()` (or a
  `set_freq()` next to a running sibling) asking for another bucket is refused at compile
  time, where it is written, through the new `claim()` intrinsic in `pymcu.types` (#300).

### Classes and variables
- A driver takes a list and keeps it. A list of instances (`Bar([Pin("PD5", Pin.OUT),
  Pin("PD6", Pin.OUT)])`) or of numbers, given to a constructor or to a method and stored
  in a `self` field, is now a compile-time sequence the field is another name for:
  `self._pins[0]`, `for p in self._pins`, `len(self._pins)` and a run-time
  `self._pins[i].method()` all answer. Before, the field became a scalar and every read
  was zero, or the spelling was refused outright (#313, #314).
- A list argument is built once. It used to stay raw AST bound to the parameter, so every
  subscript re-evaluated it and `ps[0]` ran the constructor again. A method call through
  the rebuilt instance still reached the right pin, because the duplicate folds to the
  same port and bit; a property setter did not, because it resolves its receiver to a
  name and an anonymous re-construction is not one, so the write was dropped with no
  diagnostic (#313).
- A bytearray handed to a driver and stored in a field keeps its storage, so
  `self._data[i] = v` writes the caller's buffer instead of being refused as a bit index
  into a scalar (#315).
- Assigning to a name the class defines as a METHOD is refused where it is written,
  instead of writing a phantom field that shadows the method: `p.value = 1` on a HAL Pin,
  the CircuitPython spelling, built clean and emitted no write to the port at all. The
  refusal names `p.value(1)` and `p.value()` (#316).
- A MicroPython driver library compiles unmodified. Measured on
  `github.com/kritishmohapatra/micropython-sevenseg`, which needed eight edits and now needs
  none: a comprehension of instances over a list of pin numbers (#332), a constant subscript
  of that list (#333), an optional peripheral guarded by a field set to None (#334), a dict
  literal in a field (#335) whose values are lists (#336), and `zip` over the field of pins
  against a row of that dict (#337).
- A glyph table keyed by CHARACTERS works: a one-character string literal folds to its
  character code, so any set of distinct constant integer keys is the same rectangle, with
  the lookup mapping the key to its row index -- a subtraction when the keys are
  contiguous, a search over a key row in flash otherwise, so a 96-glyph font costs the same
  code as a 7-glyph one. A run-time key against one-character keys is allowed, because
  those are codes; a multi-character key is an interned id and stays constant-only, which
  the refusal now says. A key that matches nothing raises KeyError, where the contiguous
  case used to read past the table (#338).
- A dict of constant rows -- the shape of every digit, font and gamma table -- is a
  rectangle in flash, laid out only when a run-time key needs it. A constant key folds to
  the row and emits nothing (#336).
- `zip` walks whatever a `for` loop walks: a fixed array by name, a compile-time sequence of
  instances held in a field, a list of constants, or a dict row (#337).
- A branch that cannot be taken is not lowered. `if self.dp:` on a field holding None, and
  the same as a ternary, used to be lowered on the dead side and fail inside it (#334).
- A method that reads a compile-time table from a field is inlined rather than compiled as
  a shared subroutine, where the table is unreachable and the reader was told the method
  could not be dispatched.
- A lookup table written as a plain list reads at run time. `DIGITS = [0x3F, 0x06, ...]`
  then `DIGITS[digit]` is how every 7-segment table, font and gamma curve is written, and
  it was refused: the list lives as separate variables, which have nothing to index. The
  values are constants and nothing writes them, so the table is materialised in flash at
  the first run-time subscript that needs it, and a table only ever indexed with a
  constant still emits nothing. The same answer through a parameter and through a `self`
  field. A table the program stores into keeps its refusal, because flash cannot be
  written (#317).
- A method call on a parameter annotated with an imported class resolves to that class
  even when the argument was built by an unannotated factory function.
  `i2c = board.I2C()` -- `def I2C(): return _board_i2c(SCL, SDA)`, the generated board.py
  spelling -- declares no return type, so the factory stayed an outlined subroutine whose
  result carried no class, and `def probe(b: busio.I2C)` followed by `b.try_lock()`
  flattened the receiver's own name into the undefined `i2c_try_lock`. A plain function
  whose body returns a ZCA construction now expands at the call site when the class has
  no ABI return form -- the same lowering a declared `-> busio.I2C` factory already got.
  Both spellings of the annotation (`b: busio.I2C` and `from busio import I2C` then
  `b: I2C`) work under both front ends.

### Correctness (silent-miscompile class)
- A class-level `_BUFFER = bytearray(N)` reached through an instance now iterates and
  enumerates as the array it is (`for b in self._BUFFER`, `enumerate(self._BUFFER)`),
  and a whole-attribute read (`bus.write(self._BUFFER)`, `x = c._BUFFER`) names the
  shared storage instead of a scalar placeholder nothing ever wrote (#442).
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
- A module-level string no longer shadows a same-named parameter or local: `b = "world"`
  next to `def f(b: uint8)` made `print(b)` inside the function write "world". The local
  wins, in print, in `==`/`in` folds and in concatenation (#438).
- `a + b` of two string VARIABLES folds their texts: it added the two interned ids as
  integers, so `c = a + b` after `a = "hello"; b = "world"` printed another string's text
  or a bare id. `x == "abc"` on a non-literal `x` folds False only when `x` is not a
  string at all (#438).

### Language
- `x in range(a, b[, s])`, `reversed(range(...))` and `enumerate(range(...))` over runtime
  bounds are supported; `range()` as a value is refused with a message that names the
  spellings that work (#288).
- A module-level `main()` says where the entry point's body runs: the statements written
  after it run after that body, as they do in CPython. The call used to be dropped, so
  `main(); print("END")` printed END first and main's output last, with nothing reported.
  A second `main()` and a `main()` that returns early with module-level code after the
  call are refused where the call is written (#301).
- A method called on the name bound by `with ... as` reaches the manager's class. The name is
  a pure alias of the manager and only the field path followed it, so
  `with digitalio.DigitalInOut(board.D6) as pin:` then `pin.switch_to_output(True)` was
  refused as a call to an undefined `pin_switch_to_output` (#305).
- `None` passed as an argument, or assigned through a property setter, binds the parameter as
  `None` instead of leaving it unbound, and a `match` whose subject is `None` is decided at
  compile time: it matches `case None` and the wildcard, and only that arm is lowered.
  `pin.pull = None`, CircuitPython's spelling for "no pull", was refused with "Pull-down
  resistor not supported on AVR" from the arm the program never selected (#306).

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

### Native modules
- `pymcu natmod` builds a CircuitPython/MicroPython native module (.mpy) for the RP2040
  and RP2350: a file of annotated top-level functions becomes a relocatable object the
  interpreter imports at runtime. The C adapter between the interpreter and the kernels is
  generated from the signatures, not written by hand, because py/dynruntime.h is the only
  statement of the runtime fun-table's layout and that layout is version-specific -- going
  through the header turns a layout change into a compile error. Integers are unboxed with
  a range check that raises ValueError naming the function and the parameter, buffers are
  taken by pointer through the buffer protocol, and a module that keeps state of its own
  is refused before any tool runs.
- The compiler's `--library` mode is what a file of bare functions compiles under: every
  top-level function is a root under its source name, and a bytearray/bytes parameter
  carries its own length as a hidden trailing argument that `len()` reads -- the count can
  no longer disagree with the buffer it came from.
- An export that can raise is refused, and in library mode the refusal says "an export of
  this library" instead of telling the reader to remove a decorator they never wrote; the
  remedies it names (a constant divisor, a shift, `%`) are the ones that compile.

### Repo layout
- The language oracle corpus (`tests/oracle/`: 184 probes, the Avr8Sharp.TestKit
  runner, the pytest driver) moved to the `pymcu-avr` repo, which owns the AVR
  backend and the emulator every probe exercises. Its CI runs the suite there
  under both front ends; the `oracle` job here is retired. Probe `# doc:`
  citations still name the `docs/language/` files in this repository.

### Changed
- The `pk2cmd` programmer moved to `pymcu-pic`: the driver no longer registers
  or ships its own implementation under that name, and the plugin's
  pk2cmd-minus binary drives the PICkit 3 and PKOB in addition to the PICkit 2
  -- the PICkit 3 refusal the built-in printed on failure is gone with it.
  `pymcu new` still scaffolds `programmer = "pk2cmd"` for PIC chips and the
  name resolves to the plugin (`pip install pymcu-pic`, also pulled in by the
  `pic` extra). When two distributions register the same `pymcu.programmers`
  name, the plugin's entry point wins by design rather than by whichever order
  dist-info scanning returned, and an unknown programmer name is answered with
  the names actually registered plus a pointer at pymcu-pic.
### Library index
- An upstream submission is measured with every other upstream submission of the same
  `libraries.txt` in scope, so a measurement example whose imports are other upstream
  entries -- the ssd1306 simpletest importing its bus and framebuffer libraries --
  compiles instead of failing on an unresolved import.

### Experimental
- Profile-guided optimisation lands behind a flag (RFC 0010):
  `pgo = true` under `[tool.pymcu.experimental]` in pyproject.toml, or
  `PYMCU_EXPERIMENTAL_PGO=1`, unlocks `pymcu build --profile <file>` /
  `PYMCU_PROFILE` and `pymcu profile --pgo`. The latter runs a declared
  `workload.yaml` on the emulator and writes a per-block cycle profile the build
  then feeds to `pymcuc --profile` and -- since the second consumer landed --
  to `pymcuc-avr --profile`, where it orders the R2-R15 register homes by
  dynamic use (each variable's uses weighted by the profiled execution count of
  their block; unprofiled blocks count 1, never 0). The other consumer is a
  veto in the `@inline` outlining pass: a region whose block burns at least 1%
  of profiled cycles stays inline, recorded as `pgo: kept N region(s) inline`
  in the MIR. A backend that does not declare `--profile` in its `--help` is
  refused rather than silently building unprofiled. With the flag off, asking
  for a profiled build stops with a one-line error naming the flag and nothing
  is built; the driver never passes `--profile` to `pymcuc` nor
  `--emit-blockmap` to the backend. `pymcuc --profile` itself stays usable
  directly -- the flag is driver policy, not a compiler feature.
  pyyaml, which reads `workload.yaml`, is the optional `pgo` extra
  (`pip install 'pymcu-compiler[pgo]'`), not a driver dependency: a venv without
  it runs every other command, and only `pymcu profile --pgo` asks for it by name.

### Full commit log

<details>
<summary>741 commits from v0.1.0a10 to 83f05312 (2026-09-15 freeze), grouped by Conventional Commit type. 991 more commits landed on `main` between 83f05312 and 11e8bbe5 (2026-09-30, 1732 total since v0.1.0a10) -- see the "Added"/"Fixed" sections above (dated 2026-09-25 to 2026-09-30) for the condensed, by-area account of that window, and `git log 83f05312..11e8bbe5` for every individual subject.</summary>

### Added

- **pic18**: IEEE-754 soft-float, first five routines
- **pic18**: a real timebase -- millis(), micros(), and with them async
- **pic18**: print a float
- **pic14**: the PIC16F628A -- generated from the vendor header, guarded from birth
- **frontend**: raise takes a named constant -- and adjacent literals concatenate
- **frontend**: the compile-time evaluator learns numbers -- sizes select, names compare
- **chips**: flash_size for the twenty AVR parts -- with the unit contract written where it is declared
- **chips**: flash_size for PIC and CH32 -- both sources decoded, neither guessed
- **stdlib**: time.sleep takes seconds and folds to the delay
- **hal**: the pull-up and the byte count answer to their other names
- **ir**: a dunder PyMCU never calls says so where it is written
- **ir**: input() composes with a cast
- **ir**: a function can be stored in a name, not only passed
- **ir**: "...".format(x) lowers to the f-string it already is
- **ir**: a list of peripherals can be indexed with a run-time value
- **frontend**: PYMCU_PY_PARSER builds the AST with CPython's parser
- **frontend**: PYMCU_DUMP_AST turns the Python front end into an oracle
- **ir**: a for over a NAMED constant sequence unrolls, list or tuple
- **ir**: "text " + str(x) streams like the f-string it means
- **ir**: b"..." is a fixed buffer, and bytes is a spelling of one
- **parser**: bare yield and f(*xs) work; the rest name what they are
- **ir**: both spellings of `with` over a class do what they say
- **ir**: a list of instances iterates as the objects it holds
- **ir**: comprehensions read a named iterable, and s[i] prints a character
- **frontend**: yield from, expanded rather than nested
- **async**: create_task, as a compile-time set of tasks
- **frontend**: for/while ... else compiles
- **hal**: Pin(13) works on AVR, as the same pin as Pin("PB5")
- **ir**: a for over a constant range unrolls, so its variable is a constant
- **ir**: a list field in a class compiles as the array its literal describes
- **ir**: a const flash table wider than a byte is emitted, and read back
- **ir**: an imported module runs its own module level
- **frontend**: a relative import resolves, and an import error names the file that wrote it
- **frontend**: 'from m import *' binds the names the module defines
- **ir**: Base.method(self, ...) resolves to the body the bound call reaches
- **ir**: a class with __len__ and __getitem__ can be iterated
- **sdk**: device geometry becomes part of the IR contract
- **compiler**: the chip's geometry travels to the backend in the .mir
- **compiler**: the module loader records the path it resolved for each module
- **driver**: say when the resolved artifact does not match the sources
- **tools**: the ROM snapshot checks the image is for the chip it names
- **ir**: a class pattern in match/case binds and compares, instead of blaming a name it binds
- **stdlib**: math.floor, math.ceil and math.trunc
- **ir**: max(xs) and min(xs) over a sequence whose length is known
- **compiler**: a standard-library module is named as one, instead of being offered an install
- **ir**: for a, b in [(1, 2), (3, 4)] unpacks each pair
- **ir**: min and max take a key function
- **ir**: a call diagnostic points at the callee, or at the argument it blames
- **frontend**: a literal is located at its own token
- **ir**: an assignment diagnostic points at the target or value it blames
- **frontend**: a member access is located at its member name, a slice at its first colon
- **ir**: a dict is walked, and a set says why it is not
- **frontend**: a list comprehension is located at its opening bracket
- **ir**: the comprehension and tuple diagnostics point at the expression they blame
- **ir**: an expression diagnostic points at the sub-expression it blames
- **frontend**: a unary expression is located at its operator
- **ir**: a statement or control-flow diagnostic points at the node it blames
- **frontend**: break, continue, raise and def carry their own position
- **frontend**: a parameter and a class carry their own position
- **frontend**: a tuple is located where its text begins, and underlined whole
- **tools**: the snapshot keeps the assembly, not only a hash of it
- **stdlib**: the CYW43439 register map is the CYW43439's, not the RP2350's
- **stdlib**: one CYW43439 driver for the Pico W and the Pico 2 W, MCU surface named
- **stdlib**: the WiFi facade admits both RP parts
- **driver**: pico_w and pico2_w are board names
- **sdk**: DeviceConfig carries the board, and empty is a real answer
- **cli**: --board, so a program can say which board and not only which chip
- **frontend**: __CHIP__.board, the value a HAL can branch on
- **chips**: _ChipInfo declares board, with the unit contract written where it is declared
- **tools**: rom_snapshot takes --only, and marks a partial capture as partial
- **driver**: PYMCU_BACKEND_BINARY names the backend to run, and refuses a path that is not there
- **tools**: rom_snapshot takes --only and --backend-binary, and records what it ran
- **frontend**: a generator bound to a name can be iterated, and it is the same machine
- **hal**: WPA2-PSK association on the CYW43439
- **hal**: connect() joins WPA2 when given a key, instead of refusing one
- **exceptions**: four names the libraries need, and one list the backends can reach
- **frontend**: a raise message may be any call-free expression ([#262](https://github.com/PyMCU/PyMCU/issues/262))
- **frontend**: a quoted forward-reference annotation resolves like the bare name ([#261](https://github.com/PyMCU/PyMCU/issues/261))
- **struct**: the literal-format, scalar-result subset the drivers actually write
- **diagnostics**: a form head is told it needs brackets, not offered a near-miss ([#280](https://github.com/PyMCU/PyMCU/issues/280))
- **ir**: x in range(), reversed(range()) and runtime enumerate(range()) ([#288](https://github.com/PyMCU/PyMCU/issues/288))
- **hal**: an invert argument for the AVR PWM, the inverting compare output mode ([#293](https://github.com/PyMCU/PyMCU/issues/293))
- **types**: @inline dispatches its overloads by arity and type under CPython ([pymcu-micropython#7](https://github.com/PyMCU/pymcu-micropython/issues/7))
- **compiler**: bind __TIMEBASE__ when the program runs the time base ([#295](https://github.com/PyMCU/PyMCU/issues/295))
- **compiler**: claim(), a compile-time resource claim ([#300](https://github.com/PyMCU/PyMCU/issues/300))
- **hal**: a 16-bit duty entry on every PWM HAL, exact on the AVR 8-bit channels ([pymcu-circuitpython#30](https://github.com/PyMCU/pymcu-circuitpython/issues/30))
- **hal**: the ADC reports the reference it measures against
- **hal**: a dac module, which refuses on every part and names what works instead
- **ir**: one place answers what a compile-time sequence is
- **ir**: a list of instances given to a class is built once and passed by name
- **ir**: a self field keeps the sequence or the buffer it is given
- **ir**: a sequence in a field answers subscript, len and a run-time selection
- **hal**: a UART takes its frame format, and refuses one the part cannot send
- **hal**: a UART receive that counts, that has a deadline, and an ISR that resolves
- **hal**: the I2C bit rate is the frequency asked for, and reports what it clocks
- **hal**: the SPI clock and mode are the ones asked for, and reports what it clocks
- **hal**: pymcu.hal.pulse, measuring the pulses on a pin and sending a gated carrier
- **ir**: a lookup table written as a plain list can be read at run time
- **ir**: every way into a constant table reaches the same flash read
- **hal**: a Timer1 channel honours the frequency it is asked for
- **hal**: a software SPI can be retuned after construction
- **hal**: pymcu.hal.counter, how many edges arrived on a pin
- **hal**: the EEPROM says how big it is
- **ir**: a for over a list of string constants unrolls ([#308](https://github.com/PyMCU/PyMCU/issues/308))
- **ir**: a range() bound that folds to a constant decides the loop ([#326](https://github.com/PyMCU/PyMCU/issues/326))
- **hal**: a quadrature encoder is decoded by arithmetic in the interrupt
- **ir**: a comprehension of instances is the literal it stands for
- **ir**: a dict or set literal can be a field
- **ir**: a dict of constant rows is a table in flash, and a run-time key picks a row
- **ir**: zip walks whatever a for loop walks
- **ir**: zip also walks a list of constants reached by name or field
- **ir**: both subscripts of a rectangular dict written together
- **ir**: a rectangle of rows may be keyed by anything constant
- **parser**: an annotation spelled module.Class is read as the class it names ([#342](https://github.com/PyMCU/PyMCU/issues/342))
- **parser**: a base class spelled module.Class is read as the class it names ([#343](https://github.com/PyMCU/PyMCU/issues/343))
- **ir**: a tuple unpack writes into attributes, not only into names ([#344](https://github.com/PyMCU/PyMCU/issues/344))
- **ir**: except (A, B) catches either, instead of asking for two clauses ([#346](https://github.com/PyMCU/PyMCU/issues/346))
- **ir**: a keyword argument binds to a base-class method ([#349](https://github.com/PyMCU/PyMCU/issues/349))
- **frontend**: an import inside a try is discovered, and the try folds to the branch the module decides ([#351](https://github.com/PyMCU/PyMCU/issues/351))
- **sdk**: DeviceConfig carries the stdout rate and whether the program owns the UART ([#340](https://github.com/PyMCU/PyMCU/issues/340))
- **driver**: forward the stdout rate and UART ownership to the backend ([#340](https://github.com/PyMCU/PyMCU/issues/340))
- **frontend**: one annotation normaliser both front ends reach, reading the CircuitPython buffer names ([#356](https://github.com/PyMCU/PyMCU/issues/356), [#357](https://github.com/PyMCU/PyMCU/issues/357))
- **ir**: an ellipsis and a Literal inside a tuple annotation are read ([#357](https://github.com/PyMCU/PyMCU/issues/357))
- **ir**: a two-index subscript binds its pair at compile time ([#352](https://github.com/PyMCU/PyMCU/issues/352))
- **types**: Optional[X], X | None and Union[X, None] are read as X
- **types**: a typing-only name is accepted where nothing reads it, refused at the first read ([#367](https://github.com/PyMCU/PyMCU/issues/367))
- **types**: a subscripted Sequence, Iterable or List is the compile-time list form ([#366](https://github.com/PyMCU/PyMCU/issues/366))
- **frontend**: a keyword dictionary and a variadic positional parameter parse, in both front ends ([#368](https://github.com/PyMCU/PyMCU/issues/368))
- **ir**: a ** argument splices its known keys into the callee's named parameters ([#368](https://github.com/PyMCU/PyMCU/issues/368))
- **frontend**: except X as e binds a name, in both front ends ([#369](https://github.com/PyMCU/PyMCU/issues/369))
- **ir**: a raise records its message and a bound name reads it ([#369](https://github.com/PyMCU/PyMCU/issues/369))
- **ir**: print() of a tuple is the text CPython prints ([#375](https://github.com/PyMCU/PyMCU/issues/375))
- **driver**: discover and stage upstream libraries at build time
- **driver**: put upstream libraries on the compiler include path
- **driver**: measure upstream library submissions for the index
- **driver**: wire upstream submissions into `pymcu index build`/`verify`
- **driver**: pymcu install/libraries accept an upstream index entry
- **ir**: a class attribute is readable through an instance, and one defining __get__ is a descriptor ([#268](https://github.com/PyMCU/PyMCU/issues/268), [#360](https://github.com/PyMCU/PyMCU/issues/360))
- **ir**: a bytearray grown by .extend() takes the largest size asked for ([#362](https://github.com/PyMCU/PyMCU/issues/362))
- **driver**: gate backend flags behind a per-binary capability probe
- **ir**: a range bound to a name is a compile-time sequence, not a value ([#363](https://github.com/PyMCU/PyMCU/issues/363))
- **ir**: synthesize a default constructor for a class with no __init__ ([#391](https://github.com/PyMCU/PyMCU/issues/391))
- **ir**: accept an annotation naming an enum member, not the enum type ([#376](https://github.com/PyMCU/PyMCU/issues/376))
- **frontend**: fold if TYPE_CHECKING: like a failed optional import
- **ir**: bytes([...]) and bytes(N) are read as a fixed byte buffer
- **ir**: bytes([...]) and bytes(N) written as a call argument reach it
- **stdlib**: add pymcu.array, a stub so import array resolves
- **ir**: array.array(typecode) is read as the list[T] it names
- **ir**: derive field layout from setters and __init__-called helpers ([#441](https://github.com/PyMCU/PyMCU/issues/441))
- **ir**: Union[A, B] on an @inline/constructor parameter resolves at its call site
- **ir**: refuse a Union argument matching none of the members, naming them
- **frontend**: treat typing as a builtin module, resolved by no file
- **test**: let an oracle probe restrict itself to one front end
- **compiler**: --library roots every top-level function of the entry file
- **driver**: `pymcu natmod` builds a CircuitPython native module
- **driver**: natmod says which exports narrow an int to 16 bits
- **driver**: the generated adapter checks what the interpreter hands the kernel
- **compiler**: a buffer parameter carries its own length in library mode
- **driver**: `pymcu monitor` is a serial console -- UART to stdout, stdin to the board, no pyserial on POSIX
- **pic14**: IEEE-754 soft-float -- the eight __fp_* routines PIC14CodeGen emits calls into
- **ir**: enumerate() past the unroll cap iterates a compile-time string's flash copy
- **ir**: the compile's stdout reports whether the arena is needed and used

### Fixed

- **riscv**: the CH32V003 register map was shifted four bytes, and SysTick lived at the ARM address
- **pic18**: the ADC never touched a register, Timer0 ran in 8-bit mode, and PWM ignored freq
- **pic18**: route the UART facade to the real driver, and print() everything
- **pic14**: the 16F18877 interrupt map belonged to the 877A, and RX did not exist
- **time**: millis() returned a silent constant zero on boards with a hardware timer
- **pic18**: a conditional branch in floor overran its reach
- **pic14**: cross-check every chip map against the vendor header -- and fix the 17 lies it found
- **hal**: compile guards that actually guard -- CompileError, never NotImplementedError
- **frontend**: compile guards work in both directions -- three holes, one visit order
- **pic12**: phase 0 hygiene turns out to be the 10F200's whole GPIO and timer
- **stdlib**: print_float printed garbage past 6553.5 on RP2040/RP2350
- **frontend**: the bare register name means one thing -- the address
- **frontend**: a guard inside a runtime while no longer fires at compile time
- **frontend**: compile_isr failures blame the source, name the place, and tell the truth about where
- **avr**: two missing @inline decorators held six cells hostage
- **chips**: RAM_START lied on two PICs -- and the checker now reads source, not cache
- **frontend**: -> None parses, and scientific notation lexes
- **ir**: a function taking a class instance is expanded, not dropped
- **ir**: a raise after a search loop is not an unconditional raise
- **ir**: an instance argument never binds to a numeric parameter
- **ir**: a chip file under the project root gets a __module_init like any user module -- and its device_info() annotation no longer compiles as a call to a function that does not exist
- **ir**: an undefined name is an error, not an unwritten slot
- **ir**: the __main__ guard calls the entry point, which is not recursion
- **ir**: an alias for a builtin still reaches the builtin
- **hal**: a pin can be driven from a value computed at run time
- **ir**: a string converted to a number is parsed, or refused by name
- **driver**: a stdlib module answers to the name Python programs type
- **hal**: an unknown pin name says what the HAL takes and where numbers live
- **ir**: an unannotated integer costs what the annotated one costs
- **ir**: a module-level integer takes the width of every value it holds
- **ir**: an unexported name is reported as an import, not as a symbol
- **ir**: a run-time index on an unrolled array blames the array, not the subscript
- **ir**: a return is checked against its own function, not the caller
- **driver**: a missing 'board' points at the board line, not the package
- **ir**: a list on a non-AVR target is refused in the program's own words
- **ir**: a duplicate method name is refused instead of dropping one
- **ir**: text prints as text, and chr(n) prints as a character
- **ir**: an outlined method sees its instance and its defaults
- **ir**: a loop body may not fold constants it overwrites on the next pass
- **ir**: carry the extern signature the IR generator already fills in
- **ir**: the condition of a while may not be folded either
- **ir**: a call updates the fields it writes, including a held instance's
- **ir**: a method that writes a field and returns a value needs a slot
- **ir**: @outline asks for a shared body, it cannot invent one
- **ir**: an annotation that names no type is an error, not a uint8
- **ir**: two constant answers the compiler used to invent
- **ir**: signed values keep their sign through promotion and comparison
- **ir**: a function that promises a value must produce one on every path
- **ir**: an array keeps the initializer elements the folder cannot reduce
- **async**: a for loop in a coroutine writes the same name the rest of it reads
- **frontend**: name `yield from` wherever it is written, and print a str field
- **async**: a constant for-range step may be an expression, not just a literal
- **ir**: `in` over a named list, and the field of an instance a factory returned
- **frontend**: a `yield` whose value is consumed says so, whichever form it is
- **ir**: __bool__ answers wherever a truth value is asked for
- **ir**: a method that takes another instance cannot be a shared body
- **ir**: an operator finds its dunder even when the method is outlined
- **stdlib**: print() of a float above 21474836.48 prints the number
- **ir**: a module-level object mutated from a function keeps what was written
- **driver**: a diagnostic names the file the user wrote
- **ir**: a module-level object answers from any function, read or written
- **opt**: strength reduction leaves floats alone
- **ir**: a class defined in an imported module can be constructed
- **async**: every async diagnostic says where, and names the Python
- `if 3 & 1` took the else branch, and a module array lost its bytes
- **hal**: each AnalogPin selects its own channel on every conversion
- **async**: a coroutine method is refused by name, at its definition
- **ir**: int32's own minimum can be written
- **ir**: an overloaded constructor survives a facade re-export
- **ir**: a list field in a class is refused by name, with the form that works
- **ir**: a Python builtin is named as one, and bool() works
- **frontend**: an import from a namespace package says where the name lives
- **ir**: unpacking diagnostics name the starred target and print both sizes
- **frontend**: the last unsupported forms name themselves, and **= works
- **ir**: an @inline parameter carries the argument of its own call
- **ir**: the byte offset into a wide flash table is widened before it overflows
- **hal**: an irq trigger the chip cannot do is refused, not silently inverted
- **ir**: an unannotated buffer parameter is indexed as bytes, not as register bits
- **ir**: dividing a float by zero raises, as it does for integers
- **ir**: a call with too many arguments is refused, in a constructor too
- **hal**: the UART divisor comes from the clock and the rate, not a table
- **ir**: a folded `and` / `or` defines the label it already jumped to
- **hal**: Pin.pulse_in() on a chip without it refuses instead of measuring zero
- **driver**: a compiler killed by a signal says so, instead of blaming the program
- **compiler**: an internal error names the exception that caused it
- **async**: uasyncio is accepted as the import async def requires
- **ir**: a function may return a class instance, by being expanded where it is called
- **ir**: restore the write half of the buffer-parameter alias fix, and pin it
- **hal**: the UART a chip gets is the one that chip has
- **ir**: a module guard reaches a name read, not only a call
- **ir**: a module's init is lowered before the functions that read what it binds, and a script runs it at all
- **frontend**: a function defined twice in an imported module is an error
- **stdlib**: delete the dead second copy of the DS18B20 AVR driver
- **ir**: a multi-return @inline is a run-time value, so the store that consumes it is emitted
- **ir**: a compile-time string reaching a call through a function or a field selects by its type
- **ir**: a str rebound on another path keeps its id, and print picks the text
- **ir**: deciding whether a base call's receiver is an instance stops evaluating it
- **stdlib**: delete the orphaned servo HAL copy, and pin that no copy comes back
- **drivers**: lcd.set_cursor adds the column to the row base
- **drivers**: SSD1306.print_str takes const[str] so it compiles
- **hal**: PWM duty 0 turns the output off instead of writing BOTTOM
- **drivers**: five drivers refuse a pin they cannot drive
- **frontend**: importing a name a module does not bind is an ImportError
- **hal**: a Timer1 PWM duty clears the compare high byte before the low one
- **ir**: `from m import g` reads the slot m writes, instead of a second one of its own
- **ir**: a function's own array stops overwriting a module array of the same name
- **diagnostics**: the caret points at the name the error is about
- **ir**: the entry file's main is lowered before the functions that read what it builds
- **frontend**: device_info sizes given as constants are read, not dropped
- **driver**: the snippet gutter carries the same line numbers as the header
- **ir**: a diagnostic raised inside an imported module names that module's file
- **frontend**: the nine unsupported generator forms are refused by name
- **ir**: a name bound to an instance shadows a class of the same name
- **ir**: a method that writes no field leaves a module-level object's fields constant
- **ir**: the reflected operators dispatch, and the augmented ones that did not now do
- **ir**: assigning a field the class does not have is refused outside __init__
- **ir**: a shared body two contexts can re-enter is refused, not miscompiled
- **ir**: a module-level global keeps its widened width when read through an @inline
- **ir**: a diagnostic about an inlined body names that body's file and line
- **tools**: the dSYM exclusion excluded nothing, and the search missed the pipx copy
- **frontend**: nonlocal with no enclosing function is refused, in both front ends
- **ir**: a module's debug listing shows that module's source, not the entry file's
- **ir**: a string method is refused as a string method, not as a nested ZCA field
- **ir**: a base-class call carries back the multi-field class it returns
- **tools**: base drift could not see a file a patch deletes
- **ir**: a class-qualified callee resolves the class, not the enclosing prefix
- **frontend**: a dotted decorator is only a property modifier when it says setter or getter
- **diagnostics**: a refused argument gets the caret, for the drivers that check at construction
- **ir**: the outliner knows Base.method(self, ...) is a base call, not a self passed by value
- **ir**: a write through a field that holds an instance reaches storage, and the read stops folding
- **compiler**: a rejected keyword names what the user wrote, and a module advertises its API
- **ir**: a method on a set literal names the set, and the dict sibling names its receiver too
- **ir**: subscripting a set literal is refused instead of testing a bit of an undefined slot
- **ir**: a constant one branch assigns is not believed while a sibling branch reads it
- **ir**: a string kept in a field is a string, whatever its length and however deep
- **ir**: str.join names the part of the call it cannot build
- **async**: a coroutine keeps the object it only calls methods on
- **async**: a gather inside a gather is refused, and the docstring stops advising it
- **frontend**: the '=' in an f-string labels the value it prints
- **ir**: a DebugLine inside an inlined body names the file its line belongs to
- **ir**: a module-level global assigned from inside a function keeps the width it is given
- **frontend**: the except header names what it does not support, in both front ends
- **ir**: print and input refuse an unknown keyword, like every other call already does
- **ir**: a comparison or a return over array storage is refused instead of answering blind
- **ir**: a one-character string compares equal to itself
- **frontend**: a parser error is its own error, and '...' is the pass it means
- **diagnostics**: no diagnostic passes 1 as a stand-in for a column it does not know
- **frontend**: a binary expression is located at its operator
- **frontend**: the lexer counts the indentation of a bracketed continuation line
- **frontend**: '{{' and '}}' in an f-string emit one brace
- **ir**: 'in' over a tuple of strings and 'match' on a string see one character too
- **driver**: an unknown board is reported as unknown, before anything it implies
- **stdlib**: pymcu.pio no longer imports typing, so it can be imported at all
- **driver**: pymcu new and the board setter offer the name too
- **frontend**: the CPython bridge carries a column for the nodes both front ends agree on
- **ir**: a string copied to another name is still a string
- **ir**: a module-level initializer keeps its width when a function assigns the name
- **ir**: a method on an int or float local names the receiver, not a manufactured symbol
- **ir**: a name's type is looked up where the name actually lives
- **ir**: an overload is chosen by the argument's numeric kind, not by registration order
- **ir**: a local bound to a float literal is a float, not the uint8 it defaults to
- **ir**: a dict key matches by its text, so a one-character key in a name finds its entry
- **ir**: a method with no self is compiled, so calling it through the class links
- **frontend**: the two front ends say the same sentence about an oversized integer literal
- **diagnostics**: a refused argument is reported at the argument, for the drivers that validate at first use
- **ir**: a const declared without a subscript is a const
- **ir**: an ALL-CAPS global that is written gets storage, so the write is not discarded
- **frontend**: a refusal points at the construct it refuses, not at the token after it
- **ir**: two methods of one name are refused by name again, not as a mangled duplicate symbol
- **ir**: rebinding the name of a module-level def is refused, in both front ends
- **ir**: a refused loop points at the element it refuses, and abstains where it cannot
- **ir**: int32 MIN // -1 folds to what the chip computes instead of crashing the compiler
- **ir**: a false assert is refused in every spelling, and a runtime one warns instead of vanishing
- **ir**: a refused call points at the part it refuses, and withholds a caret that would lie
- **frontend**: parentheses around a range() in a for header group, as they do in Python
- **ir**: a refused assignment points at the source of the problem, and 15 sites are left listed
- **frontend**: a trailing comma ends the for-in range() argument list, as in every other call
- **ir**: an outlined method's stand-in carries the def it stands for
- **ir**: a keyword argument to a builtin is answered by one check, with three different answers
- **frontend**: the bridge underlines the token the parser marks, not the node around it
- **ir**: a diagnostic raised while binding an inlined call reports the call, not a line inside the callee
- **frontend**: async def is located at both its words, in both front ends
- **frontend**: a diagnostic about a coroutine underlines the function's introducer
- **frontend**: a parse error in an imported module names that module, with its line
- **ir**: a codegen decorator reaches the backend or is refused, never dropped
- **ir**: @extern on a method is refused instead of compiling an empty body
- **ir**: a typed error carries the file it is about, and the deliberate sites say so
- **tools**: the snapshot refuses to freeze a baseline it could not rebuild
- **tools**: the stdlib is a toolchain component, so the snapshot records and gates on it
- **ir**: the scan's diagnostics point at what they blame instead of at line 1
- **ir**: two helpers take the node their callers already have
- **ir**: a keyword argument to an overloaded @inline is refused, not silently dropped
- **tools**: the diff line says which half of the toolchain moved, without claiming the other is innocent
- **ir**: a zero slice step points at the step
- **ir**: a failed unpacking points at the source, not at the target list
- **ir**: a bytearray size and a list literal carry their own span
- **frontend**: yield marks its keyword, and one helper locates both refusals
- **ir**: the two input() refusals point at the argument, not at the statement
- **frontend**: a nested gather marks the outer gather
- **ir**: an @inline defined in the entry file tracks its own line, like an imported one
- **driver**: the vector table in the size report is measured, not assumed
- **hal**: a named board without a radio is refused; an unnamed one is not
- **stdlib**: the reason for the two explicit chip imports was wrong, and the real one is worse
- **ir**: a slice initialiser names the name that is not a fixed-size array
- **cli**: an unrecognised argument is refused instead of being eaten by the include list
- **frontend**: a non-literal raise message blames the argument, in both front ends
- **hal**: the two ATtinys with no ADC are refused instead of given the ATmega's
- **hal**: four AVR HALs refuse a chip they have no implementation for
- **frontend**: both front ends read the same type annotations, and refuse the rest together
- **hal**: the ATtiny85 timer refuses instead of answering 0
- **stdlib**: millis_init() goes through the per-chip selector like millis() already did
- **ir**: a module guard is reported where the reader can act on it, which is two answers
- **ir**: a bare module-level string constant is registered at scan time, like the annotated one
- **ir**: a generator used as a value is refused, and a yield in an uncalled lambda is seen
- **ir**: the bare generator call names the trap, and the walker it needed is deleted
- **stdlib**: the millisecond clock is offered only to parts whose registers it programs
- **frontend**: a generator loop inside a try is desugared like one outside it
- **ir**: the builtin exception list is derived rather than copied
- **ir**: the iterator messages stop claiming there is nowhere to report exhaustion
- **ir**: an array reaches a nested @inline through a method call, at any depth
- **frontend**: a pass is on the line map, and it was a missing Located() rather than a policy
- **cli**: the version table says which installation it is describing
- **tools**: the snapshot records what was checked, and its oracle stops calling "cannot look" clean
- **hal**: board pin numbers resolve per chip, so machine.Pin(13) stops driving the wrong leg
- **hal**: AVR name dispatch asks for the constant it needs, so a lost fold errors
- **scaffold**: the CircuitPython template picks a pin the board actually has
- **hal**: revert the ADC channel annotations, they refuse a shape that worked
- **frontend**: a trailing comma in a parameter list, which only one front end took
- **frontend**: from pkg import submodule, when __init__.py does not re-export it ([#264](https://github.com/PyMCU/PyMCU/issues/264))
- **ir**: resolve a constructor reached through mod.singleton.Nested(...) ([#271](https://github.com/PyMCU/PyMCU/issues/271))
- **ir**: run a class-body attribute's initializer, so it stops reading zero ([#270](https://github.com/PyMCU/PyMCU/issues/270))
- **tests**: ask whether the path is absolute, not whether it starts with a slash ([#269](https://github.com/PyMCU/PyMCU/issues/269))
- **ir**: an ALL-CAPS class attribute that the program writes is not a constant ([#272](https://github.com/PyMCU/PyMCU/issues/272))
- **ir**: refuse an enum member assignment, in all three spellings ([#273](https://github.com/PyMCU/PyMCU/issues/273))
- **ir**: give a module-level instance's array field its own storage ([#275](https://github.com/PyMCU/PyMCU/issues/275))
- **ir**: refuse an unknown annotation in the four positions that accepted it ([#278](https://github.com/PyMCU/PyMCU/issues/278))
- **ir**: an attribute read resolves against its own class, not against every class ([#276](https://github.com/PyMCU/PyMCU/issues/276))
- **ir**: the head of a bracketed annotation is checked too ([#278](https://github.com/PyMCU/PyMCU/issues/278))
- **ir**: a near-miss suggestion never proposes the name it just rejected ([#280](https://github.com/PyMCU/PyMCU/issues/280))
- **frontend**: refuse `raise X() from Y` in both front ends ([#277](https://github.com/PyMCU/PyMCU/issues/277))
- **ir**: an undefined base class is refused where it is declared ([#279](https://github.com/PyMCU/PyMCU/issues/279))
- **frontend**: a one-line suite parses, in every clause that takes one ([#250](https://github.com/PyMCU/PyMCU/issues/250))
- **ir**: size the range() counter from its bounds instead of uint8 ([#284](https://github.com/PyMCU/PyMCU/issues/284))
- **ir**: a signed runtime step picks the direction of its range loop at run time ([#286](https://github.com/PyMCU/PyMCU/issues/286))
- **ir**: the range() loop variable keeps Python's value after the loop ([#285](https://github.com/PyMCU/PyMCU/issues/285))
- **ir**: list comprehensions honour range()'s step ([#287](https://github.com/PyMCU/PyMCU/issues/287))
- **ir**: one width per variable name across the program ([#284](https://github.com/PyMCU/PyMCU/issues/284))
- **ir**: a runtime slice walks with an index sized by the array, not by its bound
- **ir**: a module-level accumulator is typed like a local, with promotion ([#289](https://github.com/PyMCU/PyMCU/issues/289))
- **ir**: a comparison is decided by the values, not by the left operand's width ([#290](https://github.com/PyMCU/PyMCU/issues/290))
- **ir**: an annotated global keeps its written width through the module scan ([#289](https://github.com/PyMCU/PyMCU/issues/289))
- **ir**: a single-field instance mutated in a method's loop keeps its value, and the method returns it ([#292](https://github.com/PyMCU/PyMCU/issues/292))
- **ir**: a field first stored from an annotated local is laid out at the local's width ([#294](https://github.com/PyMCU/PyMCU/issues/294))
- **stubs**: a redefined def comes out as @overload, not as a shadowed twin
- **ir**: an unannotated sequence literal takes the width of its widest element ([#298](https://github.com/PyMCU/PyMCU/issues/298))
- **ir**: a named tuple past the unroll limit gets the storage a list gets ([#297](https://github.com/PyMCU/PyMCU/issues/297))
- **hal**: PWM.stop() takes the channel off the pin, not the timer off the chip ([#296](https://github.com/PyMCU/PyMCU/issues/296))
- **hal**: a Timer0 PWM frequency the time base cannot share is refused at compile time ([#295](https://github.com/PyMCU/PyMCU/issues/295))
- **hal**: millis_init() leaves TCCR0A alone ([#295](https://github.com/PyMCU/PyMCU/issues/295))
- **hal**: the second channel of a timer asking another prescaler is refused at compile time ([#300](https://github.com/PyMCU/PyMCU/issues/300))
- **ir**: a module-level main() says where the entry point's body runs ([#301](https://github.com/PyMCU/PyMCU/issues/301))
- **ir**: a quoted site names the file its line belongs to ([#303](https://github.com/PyMCU/PyMCU/issues/303))
- **driver**: a line a diagnostic quotes is mapped back to the user's file ([#303](https://github.com/PyMCU/PyMCU/issues/303))
- **hal**: the 8-bit pwm_init keeps its own body instead of a flag through pwm_init_raw
- **ir**: a call whose callee produces no value is refused where the value is read ([#302](https://github.com/PyMCU/PyMCU/issues/302))
- **hal**: an AVR input has the pull it asked for, not the level it was driving ([#309](https://github.com/PyMCU/PyMCU/issues/309))
- **ir**: a method on the name bound by `with ... as` resolves through the alias ([#305](https://github.com/PyMCU/PyMCU/issues/305))
- **ir**: None reaches the parameter it is bound to ([#306](https://github.com/PyMCU/PyMCU/issues/306))
- **ir**: a match whose subject is None is decided at compile time ([#306](https://github.com/PyMCU/PyMCU/issues/306))
- **ir**: a property setter is lowered under its own file ([#306](https://github.com/PyMCU/PyMCU/issues/306))
- **hal**: an ADC reading scales onto the whole 16-bit range
- **hal**: an analog input with no channel behind it is refused where it is written
- **ir**: the innermost scope answers what a name holds
- **ir**: assigning to a method name is refused instead of emitting nothing
- **hal**: an exact-frequency PWM decides off by both compare bytes
- **ir**: an import alias belongs to the module that wrote it ([#320](https://github.com/PyMCU/PyMCU/issues/320))
- **ir**: a constant match subject never matches case None ([#324](https://github.com/PyMCU/PyMCU/issues/324))
- **ir**: a keyword argument clears the None an earlier expansion left ([#324](https://github.com/PyMCU/PyMCU/issues/324))
- **ir**: a field is as wide as the value assigned to it ([#322](https://github.com/PyMCU/PyMCU/issues/322))
- **ir**: one routine at two interrupt vectors is refused, not dropped ([#325](https://github.com/PyMCU/PyMCU/issues/325))
- **frontend**: a submodule named in the package's comments is still importable ([#323](https://github.com/PyMCU/PyMCU/issues/323))
- **ir**: a constant inside a nested class can be read ([#319](https://github.com/PyMCU/PyMCU/issues/319))
- **ir**: a HAL module can put its own interrupt on a pin ([#321](https://github.com/PyMCU/PyMCU/issues/321))
- **ir**: a refused comprehension says which thing is unsupported ([#307](https://github.com/PyMCU/PyMCU/issues/307))
- **driver**: the preamble is mapped per line, not by one offset ([#311](https://github.com/PyMCU/PyMCU/issues/311))
- **hal**: the accessors that declare a value produce one ([#312](https://github.com/PyMCU/PyMCU/issues/312))
- **hal**: the pulse interrupt cannot raise, and the exact PWM frequency is right unfolded
- **ir**: a call argument that holds a constant binds the parameter as one ([#327](https://github.com/PyMCU/PyMCU/issues/327))
- **ir**: what a local holds stops being true at every write to it ([#327](https://github.com/PyMCU/PyMCU/issues/327))
- **ir**: what an if-chain leaves is what every path agrees on ([#327](https://github.com/PyMCU/PyMCU/issues/327))
- **ir**: a local computed from another local carries its value too ([#327](https://github.com/PyMCU/PyMCU/issues/327))
- **ir**: a constant subscript of a named constant list is the constant
- **ir**: a branch that cannot be taken is not lowered
- **ir**: a method that reads a compile-time table is not outlined
- **ir**: a one-character key is a code, so a run-time key may match it
- **ir**: an unhandled raise in the entry function halts instead of returning ([#339](https://github.com/PyMCU/PyMCU/issues/339))
- **ir**: a definition is not a write, so two tables of the same name both fit
- **parser**: a list literal accepts the trailing comma its neighbours already take ([#341](https://github.com/PyMCU/PyMCU/issues/341))
- **parser**: an annotation is read whole, so the refusal names the construct ([#345](https://github.com/PyMCU/PyMCU/issues/345))
- **ir**: a deferred check names the module the definition is in, not the entry file ([#347](https://github.com/PyMCU/PyMCU/issues/347))
- **ir**: super().__init__ applies the base constructor's defaults ([#350](https://github.com/PyMCU/PyMCU/issues/350))
- **frontend**: the handler's own imports load when the optional one is absent ([#351](https://github.com/PyMCU/PyMCU/issues/351))
- **ir**: a module-level annotated global is swept by the unknown-name check ([#348](https://github.com/PyMCU/PyMCU/issues/348))
- **parser**: a two-index subscript is named, not reported as a missing bracket ([#352](https://github.com/PyMCU/PyMCU/issues/352))
- **hal**: a bare ATtiny that refuses a pin names the pins it has
- **ir**: a guard comparing two @inline calls folds, and the advice names what is undecided ([#330](https://github.com/PyMCU/PyMCU/issues/330))
- **ir**: a write through a name bound to a tuple is refused ([#299](https://github.com/PyMCU/PyMCU/issues/299))
- **ir**: a compile-time __len__ resolves through a name and one method hop ([#329](https://github.com/PyMCU/PyMCU/issues/329))
- **ir**: a receiver's class is resolved through the hop, so a read it cannot have is refused ([#318](https://github.com/PyMCU/PyMCU/issues/318))
- **ir**: a load retires the constant the name held, so an accumulator stops reading its seed ([#359](https://github.com/PyMCU/PyMCU/issues/359))
- **ir**: a numeric receiver keeps its name in the message when its value is known
- **ir**: a printed value keeps the width its name was declared with ([#331](https://github.com/PyMCU/PyMCU/issues/331))
- **ir**: the names an absent optional import would have bound reach the annotation reader ([#366](https://github.com/PyMCU/PyMCU/issues/366))
- **ir**: the optional-import flag folds, so a library stops compiling the branch it does not use ([#372](https://github.com/PyMCU/PyMCU/issues/372))
- **ir**: a keyword value that is not a literal is pinned where every prefix can read it ([#368](https://github.com/PyMCU/PyMCU/issues/368))
- **ir**: an omitted float default is received, and its value reaches the field ([#374](https://github.com/PyMCU/PyMCU/issues/374))
- **driver**: measure_upstream_example could not discover its own submission
- **driver**: upstream measurement needs a board for circuitpython/micropython
- **ir**: a printed line runs its operands before it writes any of its text ([#371](https://github.com/PyMCU/PyMCU/issues/371))
- **ir**: a module-level float keeps the value it was written with ([#379](https://github.com/PyMCU/PyMCU/issues/379))
- **ir**: a name bound to an instance keeps being that instance across a label ([#259](https://github.com/PyMCU/PyMCU/issues/259))
- **ir**: a dotted call to a module function does not consume a receiver slot ([#381](https://github.com/PyMCU/PyMCU/issues/381))
- **ir**: a descriptor is found through self and through an imported class ([#360](https://github.com/PyMCU/PyMCU/issues/360))
- **ir**: a field holding an instance is true or false the way its class says ([#385](https://github.com/PyMCU/PyMCU/issues/385))
- **stdlib**: exempt board pin tables from the HAL universality scan
- **ir**: a conditional expression asks a field's class before asking about None ([#385](https://github.com/PyMCU/PyMCU/issues/385))
- **ir**: bool() asks the object for its truth value ([#385](https://github.com/PyMCU/PyMCU/issues/385))
- **ir**: a name bound only to None takes its width from the value stored in it ([#385](https://github.com/PyMCU/PyMCU/issues/385))
- **ir**: a fresh local in an expanded body is as wide as what it holds ([#385](https://github.com/PyMCU/PyMCU/issues/385))
- **frontend**: a `bytes` parameter is the byte buffer it names, not a class
- **ir**: `@used` is refused where there is no subroutine to export
- **ir**: a float field's bytes are reinterpreted, not shifted
- **ir**: an allocation reads its size, so the size is not dead code
- **hal**: size the AVR pulse capture ring to the maxlen asked ([PyMCU#406](https://github.com/PyMCU/PyMCU/issues/406))
- **ir**: a large constant .extend() loops instead of unrolling ([PyMCU#411](https://github.com/PyMCU/PyMCU/issues/411))
- **ir**: `uint64` and `int64` are refused instead of stored in one byte
- **ir**: a field of a boxed instance is read from its slot, not from a flattened name
- **ir**: recognize bytearray() as a field-assignment target inside __init__
- **ir**: recognize bytearray() written inline as a call argument
- **ir**: a two-index dunder's own computed result reaches its caller
- **ir**: demote an outlined method whose self-call cannot dispatch statically ([#373](https://github.com/PyMCU/PyMCU/issues/373))
- **ir**: check the right parameter's type for a typing-only annotation
- **ir**: resolve and scan a base class across a module boundary ([#420](https://github.com/PyMCU/PyMCU/issues/420))
- **ir**: qualify a with-block's bound name like the object it names ([#390](https://github.com/PyMCU/PyMCU/issues/390))
- **ir**: a method call that returns a class instance tags its target ([#421](https://github.com/PyMCU/PyMCU/issues/421))
- **ir**: resolve a factory method's return class in its own module ([#421](https://github.com/PyMCU/PyMCU/issues/421))
- **ir**: mangle a dotted submodule alias's member with underscores ([#422](https://github.com/PyMCU/PyMCU/issues/422))
- **ir**: a list[T] parameter reaches the call-site expansion list[T] already has
- **ir**: a list-returning call types its result as a GC pointer, not UNKNOWN
- **hal**: correct pulse_in's cycles-per-iteration conversion on AVR
- **ir**: isinstance() on a ZCA instance folds to a compile-time constant ([#424](https://github.com/PyMCU/PyMCU/issues/424))
- **diagnostics**: word the no-field/no-attribute errors like the interpreter's AttributeError ([#441](https://github.com/PyMCU/PyMCU/issues/441))
- **ir**: name a zero-field class in its own no-attribute refusal ([#441](https://github.com/PyMCU/PyMCU/issues/441))
- **ir**: revert naming a zero-field class from classDirectMethods ([#441](https://github.com/PyMCU/PyMCU/issues/441))
- **ir**: a bare const parameter is an unknown kind, not "other" ([#441](https://github.com/PyMCU/PyMCU/issues/441))
- **ir**: thread a factory handle into its flattened field name too
- **ir**: forward the enclosing self into a nested @inline function
- **ir**: read back the lazily-created result temp of a super() call
- **ir**: a class field named value is not always a live slot
- **ir**: a promoted single-field slot is a live slot too
- **oracle**: measure with this checkout's own venv, and track the isinstance probe to #386
- **ir**: a property returning a zca instance keeps it dispatchable
- **diagnostics**: a library export is not told to remove a decorator it never had
- **driver**: follow the backend's widened native-module arch tuple
- **driver**: natmod says so when the ARM backend has no native-module mode
- **driver**: the pyelftools probe does not import from the project directory
- **driver**: the __bad_interrupt stub counts with the vector table, not as user code
- **driver**: a bytearray built from a literal is not an arena allocation --
  bytearray([0x15, 0x2A]), bytearray(b"..."), bytearray("...") and
  bytearray((1, 2)) tripped the runtime-size scan into injecting pymcu.arena
  (two uint16 globals plus the SRAM reservation) for a program that never
  allocates at run time; the false positive dates to 91c57e5b
- **ir**: a same-depth expansion's multi-text mark is callee-local too
- **driver**: the compiler says whether the program uses the arena, so a
  foldable bytearray size reserves nothing

### Performance

- **async**: one shared _start per coroutine instead of one per await site
- **hal**: one WS2812 emitter per pin, so a bit stops paying for the dispatch
- **ir**: a read of a known function-local folds ([#331](https://github.com/PyMCU/PyMCU/issues/331))
- **stdlib**: a uint32 is printed by one digit loop, not nine unrolled ones
- **avr**: the signed 32-bit division runtime is its own file
- **stdlib**: a float's fraction is printed without the division runtime

### Changed

- **pic12**: guard messages become named constants
- **hal**: phase 1 -- the seven UART text writers, defined once
- **hal**: fold join_open into _ioctl_send so it lands on the sequence counter
- **ir**: name the two halves of the compile-time array unroll
- **hal**: a pin's interrupt registers and its handler go on separately
- **hal**: the WS2812 emitter moves into the HAL
- **driver**: share the library index cache path with core.libraries

### Documentation

- a free function can take a class instance
- **stdlib**: device_info states where its sizes go and what omitting one means
- the language surface that changed tonight
- @staticmethod is not supported, and the roadmap's parenthetical was false
- the workflow step points at the roadmap and limitations files that exist
- one exception type per handler, and why
- **readme**: the banner announced alpha 3 while PyPI served a10
- **readme**: let pymcu new do the setup it already does
- **readme**: link the Arduino Project Hub write-up
- **ir**: the inlined-raise site says why it does not pass the node it has
- **ir**: the AugOp default arm says who it is for
- **hal**: the CYW43 firmware note named a blocker that no longer exists
- add Sponsors section with Adafruit
- say which backends are beta, and stop claiming the compat layers are stable
- **compat**: CircuitPython pages tell the truth about sleep_ms
- **compat**: the SPI block uses the real busio API
- give Adafruit the Ecosystem Partner logo the tier promises
- **language**: eight corrections that still measure as wrong
- SoftSPI and SoftI2C live in their own modules, and take Pin objects
- state the $300 goal and list sponsors in SPONSORS.md
- **language**: the range() counter is sized from its bounds, and the spellings that now work
- **language**: widths of module accumulators, comparisons by value, and the range fixes' companions
- a named tuple iterates like the named list, at any length ([#297](https://github.com/PyMCU/PyMCU/issues/297), [#298](https://github.com/PyMCU/PyMCU/issues/298))
- stop(), start() and deinit() on the PWM HAL ([#296](https://github.com/PyMCU/PyMCU/issues/296))
- Timer0 is also the time base ([#295](https://github.com/PyMCU/PyMCU/issues/295), [#300](https://github.com/PyMCU/PyMCU/issues/300))
- the two channels of one timer share its prescaler, and claim() ([#295](https://github.com/PyMCU/PyMCU/issues/295), [#296](https://github.com/PyMCU/PyMCU/issues/296), [#300](https://github.com/PyMCU/PyMCU/issues/300))
- main() at module level, and a quoted line the reader can open ([#301](https://github.com/PyMCU/PyMCU/issues/301), [#303](https://github.com/PyMCU/PyMCU/issues/303))
- two duty entries on the PWM HAL, one exact ([pymcu-circuitpython#30](https://github.com/PyMCU/pymcu-circuitpython/issues/30))
- a function that promises a value must produce one where it is read ([#302](https://github.com/PyMCU/PyMCU/issues/302))
- an input after an output on the AVR ([#309](https://github.com/PyMCU/PyMCU/issues/309))
- None is a compile-time value that travels to the parameter ([#306](https://github.com/PyMCU/PyMCU/issues/306))
- the ADC's spellings, scaling and reference; a page for pymcu.hal.dac
- a driver takes a list of pins, of numbers, or a buffer
- the root roadmap gains the list-given-to-a-class rows
- the rebuilt element loses a property write, not a method call
- the UART frame and deadline, the I2C rate, the SPI clock and mode
- **compat**: busio and the board bus constructors as they now behave
- a page for pymcu.hal.pulse, and pulseio on the CircuitPython page
- a lookup table written as a plain list reads at run time
- a method is not a field, and the spelling that drives the pin
- the exact-frequency Timer1 path, what frequency() reports, and the servo idiom
- **compat**: bitbangio, countio, keypad and rainbowio, and what changed in the older modules ([#29](https://github.com/PyMCU/PyMCU/issues/29))
- what changed in the silent-wrong-code and language-surface fixes
- the root roadmap carries the string unroll and the nested class too
- **compat**: rotaryio is implemented, with the rules that go with it ([#12](https://github.com/PyMCU/PyMCU/issues/12))
- a constant through a local, and a folded range bound ([#326](https://github.com/PyMCU/PyMCU/issues/326), [#327](https://github.com/PyMCU/PyMCU/issues/327))
- the delay written with locals now costs what the spelled-out one costs ([#327](https://github.com/PyMCU/PyMCU/issues/327))
- a driver library compiles as its author wrote it
- a one-character key is a code, a longer one is an id
- an unhandled raise halts wherever it is written ([#339](https://github.com/PyMCU/PyMCU/issues/339))
- what stops each Adafruit library on the Uno, and which are limits
- the two-index subscript names its construct, so no library is left on a bracket
- the unhandled-exception path enables the transmitter itself ([#340](https://github.com/PyMCU/PyMCU/issues/340))
- re-measure what stops each Adafruit library after the annotation fixes
- the annotation spellings and the __len__ reach that were lifted
- re-measure the Adafruit libraries after the Optional decision
- an RFC for the width of an unannotated integer over a constant trip count ([#364](https://github.com/PyMCU/PyMCU/issues/364))
- a local holding a known value is a compile-time value
- re-measure the Adafruit libraries after the typing-only rule
- an RFC for static **kwargs and a bounded exception object ([#368](https://github.com/PyMCU/PyMCU/issues/368), [#369](https://github.com/PyMCU/PyMCU/issues/369))
- adafruit_hcsr04 builds unmodified
- **kwargs is a compile-time mapping and a caught exception carries its message ([#368](https://github.com/PyMCU/PyMCU/issues/368), [#369](https://github.com/PyMCU/PyMCU/issues/369))
- RFC 0003 is implemented, except the field on a user-defined exception
- **library**: document upstream index entries
- **library**: publish generated HAL cross-backend parity report
- **language**: record the first full oracle run
- mark bytes([...]) / bytes(N) as a call argument as implemented
- mark import array / array.array(typecode) as implemented
- add RFC 0006, self is this, with measured inline-bloat costs
- **rfc-0006**: pin the 142 B and mixed-fold gates by fixture
- **rfc-0006**: land the two size-gate fixtures in pymcu-avr
- **language**: document field layout from setters/helpers and the read-order divergence ([#441](https://github.com/PyMCU/PyMCU/issues/441))
- mark Union[A, B] on an @inline/constructor parameter as implemented
- **oracle**: document the language-surface sweep (probes 121-178, new bugs, frontend-scoped headers)
- **driver**: the dropped-export refusal cites the defect it most often means
- document `pymcu natmod` in the driver CLI reference
- enumerate() over a compile-time string past the unroll cap, in the roadmaps,
  limitations and changelog

### Tests

- **driver**: the ipecmd/avrdude/libraries tests run on every CI platform
- **chips**: extend the vendor cross-check to the CH32V family
- **float**: assert that the bit-exact fixtures actually execute the soft-float
- **pic14**: the config word landed -- retire the xfail
- **tools**: the ROM snapshot gate -- the migration's before picture
- **tools**: the snapshot gate records provenance and classifies its dead cells
- **tools**: prose about a failure is not the failure -- keep it out of the gate
- **tools**: the migration's real baseline -- and the gate learns whose binary ran
- **tools**: strip the gate's comments -- and one drift verdict rides along
- **tools**: provenance that cannot be fooled by a dirty tree -- or by itself
- **tools**: every pin candidate keeps its reason
- **tools**: the 130/180 base -- and the AVR geometry chain anchored end to end
- **tools**: assembling is not being fine -- warnings become measured data
- **tools**: a cell that stops lying is an improvement, not a regression
- **tools**: the 145/210 base -- three repos aligned at once for the first time
- serialise the fixtures that capture Console.Error
- **ir**: pin the geometry contract from chip file to .mir
- **stdlib**: flash size is checked against the vendor, not against a backend copy
- **driver**: an error in an imported module points at the module, not the entry file
- **ir**: a DebugLine carries the text of the file its line number belongs to
- **stdlib**: a location assertion that can fail for the reason it exists
- **ir**: a string method names the string, and the advice it gives compiles
- **stdlib**: the PWM span is delimited by its two ends, not by a caller's text
- **ir**: a base call that returns a class, with two different offsets
- **compiler**: the install advice reaches only names that could be a library
- **stdlib**: for-in over pairs, with the operands chosen so only the right pairing folds
- **ir**: both spellings of a base call lower identically, not merely to the same value
- **stdlib**: the module-global width, including the direction that is still wrong
- **diagnostics**: a placeholder column of 1 fails the build
- **frontend**: operator positions, chained comparisons, continuation lines
- **ir**: call diagnostics point at the callee or the argument
- **ir**: a local bound to a float literal is typed float, in milliseconds
- **ir**: a rebound function name is refused, and a local of the same name is not
- **ir**: expression diagnostics point at what they blame
- **ir**: a refused loop reports where the refusal is, and stays silent where it must
- **ir**: the exponent diagnostic points at the unary operator
- **ir**: statement diagnostics point at what they blame, and the caret agrees with the file
- **ir**: the fold at the limits agrees with the runtime, in both spellings
- **driver**: a raise inside an inlined callee reports the call site
- **ir**: every spelling of a false assert is refused, and the two positions are pinned
- **ir**: a refused call points at what it blames, and the binding window is pinned as xfail
- **ir**: keyword statements point at their keyword, and a missing return at its def
- **frontend**: a parenthesised range is the bounded form, and a tuple is still a tuple
- **ir**: a refused assignment points at what it blames, and abstains where there is nothing to blame
- **ir**: record that one test is now the last guard on the plural-message decision
- **frontend**: the trailing comma in every arity, and the malformed commas that stay errors
- **ir**: the outlined method reports its own def, and the line it used to invent
- **ir**: the three answers a keyword argument can get, and the calls that must stay untouched
- **frontend**: the parity check compares the underline, and pins the async def gap
- **stdlib**: the eight binding-window refusals name the call site, in both front ends
- **frontend**: the coroutine introducer, measured and not assumed to be nine
- **stdlib**: a parse error in an imported module names that module
- **ir**: @naked survives outlining, and every path that expands refuses it
- **ir**: an @extern method is refused, and the module-level form still registers its symbol
- **stdlib**: the caller of an inlined refusal is not always the entry file
- **ir**: the scan and core tails, and the line they used to invent
- **ir**: the two helper diagnostics point at the expression their callers blame
- **stdlib**: all three overload spellings are refused at the call, and a plain keyword still compiles
- **frontend**: the tuple spellings, and two flip-guards collected
- **stdlib**: the entry file's own @inline names its own line
- **driver**: the size report follows the table the backend emitted
- **stdlib**: the WiFi facade says which kind of no
- **driver**: the WiFi whitelist and the board table are compared, not remembered
- **ir**: the slice initialiser's caret is under the source name
- **cli**: the refusal is asserted on the exit code, not on the message
- **driver**: the twelve spellings of a non-literal raise message agree
- **driver**: the two front ends are compared on VERDICT, not only on refusals
- **stdlib**: a module guard names the guard, or the reader's own line, and not the wrong one
- **stdlib**: a module string constant reaches a helper, and a runtime string still does not
- **stdlib**: both helper shapes are pinned, and the fixture documents how to build it wrong
- **ir**: a generator as a value, and a yield in a lambda called or not
- **ir**: the trap message is asserted where it belongs, and the lambda cases are at the front end
- **frontend**: two loops over one bound generator share one machine
- **ir**: the two builtin-exception lists are one list, checked against Codes
- **ir**: an array keeps its storage through one and two levels of nested @inline
- **frontend**: a pass has a line, and both front ends put it in the same place
- **stdlib**: a module-level accumulator follows the promoted width of its sum ([#289](https://github.com/PyMCU/PyMCU/issues/289))
- **stubs**: pin the overload shape and the compat-layer module
- **ir**: pin the tuple storage gate and the sequence element width ([#297](https://github.com/PyMCU/PyMCU/issues/297), [#298](https://github.com/PyMCU/PyMCU/issues/298))
- **ir**: pin where a module-level main() puts the entry point's body ([#301](https://github.com/PyMCU/PyMCU/issues/301))
- pin the line a diagnostic quotes for an earlier site ([#303](https://github.com/PyMCU/PyMCU/issues/303))
- **ir**: pin the unproduced-result refusal and the shapes it must not touch ([#302](https://github.com/PyMCU/PyMCU/issues/302))
- **ir**: pin a method call on the `with ... as` name ([#305](https://github.com/PyMCU/PyMCU/issues/305))
- pin the None match and where a setter's refusal lands ([#306](https://github.com/PyMCU/PyMCU/issues/306))
- **ir**: cover a list of pins, of numbers and a buffer handed to a class
- **ir**: a constant table in flash, and the two refusals that stay
- **ir**: assigning to a method name, and the two field writes that must stay
- **stdlib**: the pair refusal no longer promises integers ([#308](https://github.com/PyMCU/PyMCU/issues/308))
- **stdlib**: the values these tests call run-time are read from a register ([#327](https://github.com/PyMCU/PyMCU/issues/327))
- **stdlib**: the LCD command byte is read off the pins, not off a parameter slot ([#327](https://github.com/PyMCU/PyMCU/issues/327))
- **ir**: the six gaps a MicroPython library needed, and the two refusals that stay
- **ir**: a glyph table keyed by characters, and the key that cannot be one
- **frontend**: a tuple of exception types is asserted as accepted, not as refused ([#346](https://github.com/PyMCU/PyMCU/issues/346))
- **driver**: the scaffolded CircuitPython blink names a pin its board has
- **stdlib**: add cross-backend HAL API parity suite
- **oracle**: add a permanent CPython-vs-avr8sharp language oracle suite
- **oracle**: fix eight probes that tested the wrong thing
- **oracle**: treat a documented divergence as its own expectation kind
- **oracle**: track the 20 remaining mismatches as filed compiler bugs
- **driver**: capability gate against a fake backend declaring a subset
- **unit**: a binary outside the tree is answered by the cause, not by a missing file
- **oracle**: untrack probe 049, its bytearray field write now compiles
- **oracle**: untrack probe 078, its two-index round trip now matches
- **driver**: the range-as-a-value parity case uses the name as a value ([#363](https://github.com/PyMCU/PyMCU/issues/363))
- **unit**: add BytesLiteralArgumentTests -- bytes([...]) / bytes(N) as an argument
- **oracle**: probe orphan-method shapes that already match CPython
- **oracle**: track a class method attached after definition ([#426](https://github.com/PyMCU/PyMCU/issues/426))
- **oracle**: track a function stored in a self field ([#425](https://github.com/PyMCU/PyMCU/issues/425))
- **oracle**: track a nested @inline closure over self ([#427](https://github.com/PyMCU/PyMCU/issues/427))
- **oracle**: track a factory function's lost constructor argument ([#429](https://github.com/PyMCU/PyMCU/issues/429))
- **oracle**: track super().method() miscomputing with constant args ([#430](https://github.com/PyMCU/PyMCU/issues/430))
- **oracle**: untrack four probes fixed by #390 and #391
- **oracle**: file front-end diagnostic parity for comprehensions ([#432](https://github.com/PyMCU/PyMCU/issues/432))
- **oracle**: untrack len()/__len__ dispatch, fixed per #396
- **unit**: add ArrayArrayAsListTests -- import array / array.array(typecode)
- **ir**: field layout from setters, __init__-called helpers, and type conflicts ([#441](https://github.com/PyMCU/PyMCU/issues/441))
- **driver**: field layout parity across both front ends ([#441](https://github.com/PyMCU/PyMCU/issues/441))
- **unit**: add UnionParameterAtCallSiteTests -- Union[A, B] resolved per call site
- **oracle**: sweep numeric builtins (abs/min/max/round/casts/chr/ord/oct/len)
- **oracle**: sweep integer semantics (shift/bitwise/chained compare/augassign/width wrap)
- **oracle**: sweep string operations (concat/compare/fstring align/methods/in/slice)
- **oracle**: sweep control flow (nested break/continue, ternary, walrus, pass, nested def)
- **oracle**: sweep data model dunders (eq/le/sub/mul, str, contains, call, iter/next, isinstance)
- **oracle**: sweep collections (list for-loop, list.index, tuple indexing, dict membership)
- **oracle**: sweep exceptions (nested try, bare reraise, custom class, inline boundary)
- **oracle**: sweep functions (args/kwargs, posonly, default-from-global, inline vs plain) and pin two match-pattern front-end gaps
- **oracle**: sweep modules (import as/star, __name__ idiom, __CHIP__, sys refusal)
- **oracle**: sweep async (sleep in a loop returning a value, gather refusal)
- **ir**: a single-field factory's unannotated field threads its value ([#429](https://github.com/PyMCU/PyMCU/issues/429))
- **oracle**: untrack the factory-handle field probe, fixed per #429
- **ir**: a nested @inline closure writes through to the enclosing self ([#427](https://github.com/PyMCU/PyMCU/issues/427))
- **oracle**: untrack the nested inline closure probe, fixed per #427
- **ir**: super() plus a value-named subclass field, constant args ([#430](https://github.com/PyMCU/PyMCU/issues/430))
- **oracle**: untrack the super()-plus-field-constant-args probe, fixed per #430
- **ir**: pin --library rooting and the hidden buffer length
- **ir**: pin the library-export boundary sentence, both spellings
- **driver**: the natmod signature conversion and generated adapter
- **ir**: pin enumerate() over a long string reading the flash copy

### CI

- wire HAL parity and oracle suites into GitHub Actions

### Chore

- route backend bugs to their own repos from the issue chooser
- **tools**: a check for the two ways a measurement can be of something other than HEAD
- point the compat submodules at what is actually released

### Reverted

- **ir**: the local-read fold is withdrawn until its precondition holds ([#331](https://github.com/PyMCU/PyMCU/issues/331), [#370](https://github.com/PyMCU/PyMCU/issues/370))

### Other

- style(ir): the list-field comment sits above the branch it describes

</details>



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
