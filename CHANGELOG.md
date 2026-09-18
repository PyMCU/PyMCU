# Changelog — pymcu-compiler / pymcu-stdlib

## 0.1.0b1 (Unreleased, prepared 2026-09-15)

Beta 1 covers the frontend (parser, IR, diagnostics) and the AVR backend as a
matched pair: everything below was found or fixed compiling and running real
programs against AVR silicon or the AVR emulator, and it ships with a
regression test. The ARM/RP2040/RP2350, PIC, and RISC-V backends stay alpha
on purpose (see their own CHANGELOG entries), but every frontend fix here
applies to them too, since the frontend is shared across all backends.
`pymcu-avr` and `pymcu-circuitpython` move to `0.1.0b1` alongside this
package; `pymcu-sdk` moves in lockstep because the release gate requires the
compiler, stdlib, and SDK to publish at the same version.

**Frozen for release at `83f05312`.** The freeze waited on three silent
wrong-code bugs found by an orphan-method probe sweep the night before:
a nested `@inline` function silently lost writes it made to `self`
(#427), a factory function threaded the wrong value into an unannotated
field it returned (#429), and `super().method()` miscomputed when a
subclass field's constructor argument was a constant (#430). All three
are fixed and covered by a regression fixture; see
[State of the beta](docs/language/state-of-the-beta.md#what-the-oracle-knows-is-wrong)
for what the differential oracle still knows is wrong and discloses on
purpose, as opposed to bugs like these three that were silent until found.

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

### Silent wrong code
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

### Language surface
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

### Full commit log

<details>
<summary>All 741 commits since v0.1.0a10, grouped by Conventional Commit type</summary>

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
